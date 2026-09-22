import Foundation
import CoreFoundation

/// One app-server thread/turn, owned by ProcessRunner's actor. Only explicit,
/// single-call user decisions leave this channel; no session or policy grants.
final class CodexApprovalChannel {
    static let maximumFrameBytes = 4 * 1024 * 1024
    static let maximumInputBytes = 65_536
    static let maximumPending = 16
    private struct Pending { let rpcID: Any; var display: ToolPermissionRequest }
    private let runId: String
    private let request: StartRunRequest
    private let workspacePath: String
    private let attachments: AttachmentPreparation
    private let write: (Data) -> Void
    private let event: (Data) -> Void
    private let emit: (ToolPermissionRequest) -> Void
    private let activity: (ToolPermissionRequest, String) -> Void
    private let warning: (String) -> Void
    private let fail: (String) -> Void
    private let completed: () -> Void
    private var buffer = Data()
    private var pending: [String: Pending] = [:]
    private var seen = Set<String>()
    private var files: [String: [String: Any]] = [:]
    private var rpc: [String: String] = [:]
    private var sequence = 0
    private var threadId: String?
    private var turnId: String?
    private var awaitingTurn = false
    private var closed = false
    private var started = false
    private var ready = false
    private var usage: [String: Any] = [:]
    private(set) var initialized = false
    private(set) var turnCompleted = false
    private(set) var failed = false

    init(runId: String, request: StartRunRequest, workspacePath: String, attachments: AttachmentPreparation,
         write: @escaping (Data) -> Void, event: @escaping (Data) -> Void,
         emit: @escaping (ToolPermissionRequest) -> Void, activity: @escaping (ToolPermissionRequest, String) -> Void,
         warning: @escaping (String) -> Void, fail: @escaping (String) -> Void, completed: @escaping () -> Void) {
        self.runId = runId; self.request = request; self.workspacePath = workspacePath; self.attachments = attachments
        self.write = write; self.event = event; self.emit = emit; self.activity = activity
        self.warning = warning; self.fail = fail; self.completed = completed
    }

    func start() {
        guard !started, !closed else { return }; started = true
        call("initialize", ["clientInfo": ["name": "mightyclaude", "title": "MightyClaude", "version": "1.0"],
                            "capabilities": ["experimentalApi": false]])
    }
    func initializationTimedOut() {
        guard !closed, !ready else { return }
        failClosed("Codex 승인 채널 초기화 시간이 초과되었습니다.")
    }
    func receive(_ data: Data) {
        guard !closed else { return }
        // Bound incomplete frames even when a peer streams data without newlines.
        for byte in data {
            if byte == 10 {
                let frame = buffer; buffer.removeAll(keepingCapacity: true)
                if !frame.isEmpty { receiveFrame(frame) }
                if closed { return }
            } else {
                guard buffer.count < Self.maximumFrameBytes else { failClosed("Codex 응답 프레임 크기 제한을 초과했습니다."); return }
                buffer.append(byte)
            }
        }
    }
    /// EOF is not success: a terminal turn/completed notification is required.
    func flush() {
        guard !closed else { return }
        if !buffer.isEmpty { let frame = buffer; buffer.removeAll(); receiveFrame(frame) }
        if !closed { failClosed("Codex 승인 채널이 응답 완료 전에 종료되었습니다.") }
    }
    private func receiveFrame(_ data: Data) {
        guard let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            failClosed("Codex 승인 채널 응답 형식이 올바르지 않습니다."); return
        }
        if let method = obj["method"] as? String {
            guard let params = obj["params"] as? [String: Any] else {
                if let id = obj["id"] { rpcError(id, "Invalid request parameters.") }
                else { failClosed("Codex 알림 형식이 올바르지 않습니다.") }; return
            }
            if let id = obj["id"] { serverRequest(id, method, params) }
            else { notification(method, params) }
            return
        }
        guard let id = Self.idKey(obj["id"]), let method = rpc.removeValue(forKey: id) else { return }
        guard obj["error"] == nil, let result = obj["result"] as? [String: Any] else {
            failClosed("Codex \(method) 요청이 실패했습니다. CLI 버전과 인증·권한 설정을 확인해 주세요."); return
        }
        switch method {
        case "initialize":
            initialized = true
            send(["method": "initialized"])
            var params: [String: Any] = ["cwd": workspacePath, "approvalPolicy": "on-request", "approvalsReviewer": "user", "sandbox": "workspace-write"]
            if request.model != "default" { params["model"] = request.model }
            if let resume = request.resumeId {
                params["threadId"] = resume; params["excludeTurns"] = true; call("thread/resume", params)
            } else { call("thread/start", params) }
        case "thread/start", "thread/resume":
            guard let thread = result["thread"] as? [String: Any], let id = thread["id"] as? String, CoreValidation.identifier(id),
                  request.resumeId == nil || request.resumeId == id else { failClosed("Codex 대화 식별자가 일치하지 않습니다."); return }
            threadId = id; legacy(["type": "thread.started", "thread_id": id]); startTurn(id)
        case "turn/start":
            guard let turn = result["turn"] as? [String: Any], let id = turn["id"] as? String, CoreValidation.identifier(id),
                  turnId == nil || turnId == id else { failClosed("Codex 실행 식별자가 일치하지 않습니다."); return }
            if turnId == nil { turnId = id; legacy(["type": "turn.started"]) }
            awaitingTurn = false; ready = true
        default: break
        }
    }
    private func startTurn(_ id: String) {
        let prompt = request.input.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ? "첨부한 파일을 확인해 주세요." : request.input
        let refs = attachments.files.filter { !$0.attachment.mediaType.hasPrefix("image/") }.map {
            "첨부 파일 \(Self.jsonString($0.attachment.name)): \(Self.jsonString($0.url.path))"
        }
        var input: [[String: Any]] = [["type": "text", "text": ([prompt] + refs).joined(separator: "\n\n")]]
        input += attachments.files.filter { $0.attachment.mediaType.hasPrefix("image/") }.map { ["type": "localImage", "path": $0.url.path] }
        var params: [String: Any] = ["threadId": id, "input": input, "cwd": workspacePath,
            "approvalPolicy": "on-request", "approvalsReviewer": "user",
            "sandboxPolicy": ["type": "workspaceWrite", "writableRoots": [workspacePath], "networkAccess": request.settings.networkAccess]]
        if request.model != "default" { params["model"] = request.model }
        if request.settings.effort != "default" { params["effort"] = request.settings.effort }
        if request.settings.fastMode { params["serviceTier"] = "fast" }
        awaitingTurn = true; call("turn/start", params)
    }
    private func notification(_ method: String, _ params: [String: Any]) {
        guard let threadId, params["threadId"] as? String == threadId else { return }
        if method == "serverRequest/resolved", let key = Self.idKey(params["requestId"]) {
            let ids = pending.filter { Self.idKey($0.value.rpcID) == key }.map(\.key)
            for id in ids { cancelPending(id) }; return
        }
        if method == "turn/started", awaitingTurn, let turn = params["turn"] as? [String: Any], let id = turn["id"] as? String, CoreValidation.identifier(id) {
            guard turnId == nil || turnId == id else { failClosed("Codex 실행 알림이 현재 실행과 일치하지 않습니다."); return }
            if turnId == nil { turnId = id; legacy(["type": "turn.started"]) }; return
        }
        if method == "turn/completed" {
            guard let turn = params["turn"] as? [String: Any], let turnId, turn["id"] as? String == turnId else { return }
            guard turn["status"] as? String == "completed" else {
                legacy(["type": "turn.failed", "error": turn["error"] ?? ["message": "Codex 실행이 완료되지 않았습니다."]])
                failClosed("Codex 실행이 실패하거나 중단되었습니다."); return
            }
            legacy(["type": "turn.completed", "usage": usage]); turnCompleted = true; cancelAll(); completed(); return
        }
        guard let turnId, params["turnId"] as? String == turnId else { return }
        if method == "error", params["willRetry"] as? Bool != true {
            legacy(["type": "error", "error": params["error"] ?? [:]]); failClosed("Codex 실행 오류가 발생했습니다."); return
        }
        if method == "thread/tokenUsage/updated", let tokens = params["tokenUsage"] as? [String: Any], let last = tokens["last"] as? [String: Any] {
            usage = ["input_tokens": last["inputTokens"] ?? 0, "cached_input_tokens": last["cachedInputTokens"] ?? 0, "output_tokens": last["outputTokens"] ?? 0]; return
        }
        guard ["item/started", "item/completed"].contains(method), let item = params["item"] as? [String: Any], let id = item["id"] as? String else { return }
        if method == "item/completed" {
            let ids = pending.filter { $0.value.display.toolUseId == id }.map(\.key)
            for pendingID in ids { cancelPending(pendingID) }
        }
        if item["type"] as? String == "fileChange" {
            if method == "item/started", files.count < 128, let bytes = try? JSONSerialization.data(withJSONObject: item), bytes.count <= Self.maximumInputBytes { files[id] = item }
            if method == "item/completed" { files.removeValue(forKey: id) }
        }
        if let mapped = Self.legacyItem(item) { legacy(["type": method == "item/started" ? "item.started" : "item.completed", "item": mapped]) }
    }
    private func serverRequest(_ rawID: Any, _ method: String, _ params: [String: Any]) {
        guard let key = Self.idKey(rawID) else { failClosed("Codex 승인 요청 식별자가 올바르지 않습니다."); return }
        guard seen.insert(key).inserted else { failClosed("중복된 Codex 승인 요청을 중단했습니다."); return }
        guard seen.count <= 2_048 else { failClosed("Codex 승인 요청 수 제한을 초과했습니다."); return }
        guard let threadId, let turnId, params["threadId"] as? String == threadId, params["turnId"] as? String == turnId else {
            rpcError(rawID, "Request does not belong to the active thread and turn."); return
        }
        if method == "item/permissions/requestApproval" {
            reply(rawID, ["permissions": [:], "scope": "turn"]); warning("추가 권한 묶음 요청은 지원하지 않아 거부했습니다. 개별 명령 승인을 사용해 주세요."); return
        }
        if method == "mcpServer/elicitation/request" { reply(rawID, ["action": "decline", "content": NSNull(), "_meta": NSNull()]); return }
        guard ["item/commandExecution/requestApproval", "item/fileChange/requestApproval"].contains(method) else {
            rpcError(rawID, "This host does not support this request."); warning("지원하지 않는 Codex 입력 요청을 거부했습니다."); return
        }
        guard let itemID = params["itemId"] as? String, CoreValidation.identifier(itemID) else { reply(rawID, ["decision": "decline"]); return }
        guard pending.count < Self.maximumPending else { reply(rawID, ["decision": "decline"]); warning("대기 중인 승인 요청 수 제한을 초과했습니다."); return }
        let isCommand = method == "item/commandExecution/requestApproval"
        var input = params
        var canAllow: Bool
        if isCommand {
            canAllow = (params["command"] as? String)?.isEmpty == false && (params["cwd"] as? String)?.hasPrefix("/") == true
                && (params["kind"] == nil || params["kind"] as? String == "command")
        } else {
            let changes = files[itemID]?["changes"] as? [[String: Any]]
            input["changes"] = changes ?? []
            canAllow = changes?.isEmpty == false && changes!.allSatisfy { ($0["path"] as? String)?.isEmpty == false && $0["diff"] is String && $0["kind"] is [String: Any] }
            // grantRoot asks for a wider lasting grant rather than this diff.
            if let root = params["grantRoot"], !(root is NSNull) { canAllow = false }
        }
        if let decisions = params["availableDecisions"], !(decisions is NSNull) { canAllow = canAllow && (decisions as? [Any])?.contains(where: { $0 as? String == "accept" }) == true }
        guard let display = Self.displayJSON(input), display.utf8.count <= Self.maximumInputBytes else {
            reply(rawID, ["decision": "decline"]); warning("전체 내용을 표시할 수 없는 Codex 승인 요청을 거부했습니다."); return
        }
        let value = ToolPermissionRequest(id: UUID().uuidString, runId: runId, toolUseId: itemID,
            toolName: isCommand ? "command_execution" : "file_change", inputJSON: display,
            summary: isCommand ? "명령 실행 승인" : "파일 변경 승인",
            reason: canAllow ? (params["reason"] as? String).map { ActivitySupport.clean($0, maximumBytes: 8_192) } : "전체 명령·변경 내용을 확인할 수 없거나 한 번 허용을 지원하지 않아 거부만 가능합니다.", canAllow: canAllow)
        pending[value.id] = Pending(rpcID: rawID, display: value); activity(value, "waiting"); emit(value)
    }
    func respond(requestId: String, allow: Bool) throws {
        guard !closed, let ask = pending[requestId] else { throw MightyError("이미 처리되었거나 종료된 승인 요청입니다.") }
        guard !allow || ask.display.canAllow else { throw MightyError("이 요청은 한 번 허용을 지원하지 않습니다.") }
        pending.removeValue(forKey: requestId)
        reply(ask.rpcID, ["decision": allow ? "accept" : "decline"])
        var value = ask.display; value.state = allow ? "allowed" : "denied"
        activity(value, allow ? "running" : "error"); emit(value)
    }
    private func cancelPending(_ id: String) {
        guard var value = pending.removeValue(forKey: id)?.display else { return }
        value.state = "cancelled"; activity(value, "stopped"); emit(value)
    }
    func cancelAll() {
        guard !closed else { return }; closed = true; buffer.removeAll(); files.removeAll(); rpc.removeAll()
        let asks = Array(pending.values); pending.removeAll()
        for ask in asks { var value = ask.display; value.state = "cancelled"; activity(value, "stopped"); emit(value) }
    }
    private func failClosed(_ message: String) { guard !closed else { return }; failed = true; cancelAll(); fail(message) }
    private func call(_ method: String, _ params: [String: Any]) {
        sequence += 1; let id = "mighty-\(sequence)"; rpc[Self.idKey(id)!] = method
        send(["id": id, "method": method, "params": params])
    }
    private func reply(_ id: Any, _ result: [String: Any]) { send(["id": id, "result": result]) }
    private func rpcError(_ id: Any, _ message: String) { send(["id": id, "error": ["code": -32601, "message": message]]) }
    private func send(_ obj: [String: Any]) { if let data = try? JSONSerialization.data(withJSONObject: obj, options: [.sortedKeys, .withoutEscapingSlashes]) { write(data + Data([10])) } }
    private func legacy(_ obj: [String: Any]) { if let data = try? JSONSerialization.data(withJSONObject: obj) { event(data + Data([10])) } }
    private static func idKey(_ raw: Any?) -> String? {
        if let text = raw as? String, !text.isEmpty, text.utf8.count <= 256 { return "s:" + text }
        if let number = raw as? NSNumber, CFGetTypeID(number) != CFBooleanGetTypeID(), number.doubleValue.isFinite, number.doubleValue.rounded() == number.doubleValue { return "n:" + number.stringValue }
        return nil
    }
    private static func jsonString(_ text: String) -> String { String(decoding: (try? JSONEncoder().encode(text)) ?? Data(), as: UTF8.self) }
    private static func displayJSON(_ obj: [String: Any]) -> String? {
        guard let data = try? JSONSerialization.data(withJSONObject: obj, options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]) else { return nil }
        return String(decoding: data, as: UTF8.self).unicodeScalars.map {
            $0.properties.generalCategory == .format ? String($0).utf16.map { String(format: "\\u%04x", $0) }.joined() : String($0)
        }.joined()
    }
    private static func legacyItem(_ raw: [String: Any]) -> [String: Any]? {
        let types = ["agentMessage": "agent_message", "commandExecution": "command_execution", "fileChange": "file_change", "mcpToolCall": "mcp_tool_call", "webSearch": "web_search", "collabAgentToolCall": "collab_agent_tool_call", "contextCompaction": "context_compaction"]
        guard let type = raw["type"] as? String, let mapped = types[type] else { return nil }
        var item = raw; item["type"] = mapped
        for (source, target) in ["aggregatedOutput": "aggregated_output", "exitCode": "exit_code", "senderThreadId": "sender_thread_id", "receiverThreadIds": "receiver_thread_ids", "agentsStates": "agents_states"] { if let value = item.removeValue(forKey: source) { item[target] = value } }
        if let status = item["status"] as? String, status == "inProgress" { item["status"] = "in_progress" }
        if type == "collabAgentToolCall", let tool = item["tool"] as? String { item["tool"] = ["spawnAgent": "spawn_agent", "sendInput": "send_input", "closeAgent": "close_agent", "wait": "wait"][tool] ?? tool }
        return item
    }
}
