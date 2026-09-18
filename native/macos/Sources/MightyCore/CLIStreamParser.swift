import Foundation
import CryptoKit

public final class CLIStreamParser {
    private let provider: String
    private let log: (String, String) -> Void
    private let resume: (String) -> Void
    private let activity: ((AgentActivity) -> Void)?
    private let control: ((Data) -> Void)?
    private let result: (() -> Void)?
    private let usageTracker: SessionUsageTracker
    private let graphTracker: ExecutionGraphTracker?
    private let activityNamespace: String
    private let activityClock: () -> TimeInterval
    private var buffer = Data()
    private var dropping = false
    private var seen: [String] = []
    private var assistantSeen = false
    private var lastResume: String?
    private var pendingText = ""
    private var pendingTextTruncated = false
    private var toolActivities: [String: AgentActivity] = [:]
    private var activityStarts: [String: TimeInterval] = [:]
    private var activityOrder: [String] = []
    private var modSequences: [String: Int] = [:]
    private var permissionStates: [String: String] = [:]
    private var lastTurn: AgentActivity?
    public private(set) var failed = false

    public init(provider: String, log: @escaping (String, String) -> Void, resume: @escaping (String) -> Void, activityNamespace: String = UUID().uuidString, activity: ((AgentActivity) -> Void)? = nil, control: ((Data) -> Void)? = nil, result: (() -> Void)? = nil, activityClock: (() -> TimeInterval)? = nil, usage: ((SessionUsage) -> Void)? = nil, graph: ((ExecutionGraphNode) -> Void)? = nil, graphInput: String? = nil) {
        self.provider = provider; self.log = log; self.resume = resume
        self.activityNamespace = activityNamespace; self.activity = activity; self.control = control; self.result = result
        usageTracker = SessionUsageTracker(provider: provider, callback: usage)
        graphTracker = MightyGraphSupport.providers.contains(provider) ? graph.map { ExecutionGraphTracker(runID: activityNamespace, input: graphInput, provider: provider, emit: $0) } : nil
        let origin = ContinuousClock.now
        self.activityClock = activityClock ?? {
            let elapsed = origin.duration(to: .now).components
            return Double(elapsed.seconds) + Double(elapsed.attoseconds) / 1e18
        }
    }
    public func push(_ string: String) { push(Data(string.utf8)) }
    public func push(_ data: Data) {
        for byte in data {
            if byte == 10 {
                if !dropping { consume(buffer) }
                buffer.removeAll(keepingCapacity: true); dropping = false
            } else if !dropping {
                if buffer.count >= 1024 * 1024 {
                    buffer.removeAll(keepingCapacity: false); dropping = true; log("system", "너무 긴 출력 한 줄을 생략했습니다.")
                } else { buffer.append(byte) }
            }
        }
    }
    public func flush() {
        if !dropping && !buffer.isEmpty { consume(buffer) }
        buffer.removeAll(); flushText()
    }
    private func flushText() {
        if !pendingText.isEmpty { log("assistant", pendingText) }
        if pendingTextTruncated { log("system", "응답 한 메시지가 128 KiB를 넘어 뒷부분을 생략했습니다.") }
        pendingText = ""; pendingTextTruncated = false
    }
    private func errorText(_ value: Any?, fallback: String) -> String {
        if let text = value as? String { return String(text.prefix(32_768)) }
        if let value = value as? [String: Any], let text = value["message"] as? String { return String(text.prefix(32_768)) }
        return fallback
    }
    private func resumeIfValid(_ value: Any?) {
        guard let id = value as? String, CoreValidation.identifier(id), id != lastResume else { return }
        lastResume = id; resume(id)
    }
    private func emitUnique(_ content: String, id: String) {
        guard !content.isEmpty else { return }
        let hash = SHA256.hash(data: Data(content.utf8)).map { String(format: "%02x", $0) }.joined()
        let key = id + ":" + hash
        guard !seen.contains(key) else { return }
        seen.append(key); if seen.count > 512 { seen.removeFirst() }
        assistantSeen = true; log("assistant", content)
    }

    private func turn(_ summary: String, state: String = "running") {
        let value = AgentActivity(id: activityNamespace, provider: provider, kind: "turn", state: state, summary: summary)
        guard value != lastTurn else { return }; lastTurn = value; activity?(value)
    }

    private func tool(id rawID: String?, name: String? = nil, input: Any? = nil, state: String, output: String? = nil, summary: String? = nil) {
        guard let rawID, !rawID.isEmpty, rawID.utf8.count <= 512 else { return }
        let id = ActivitySupport.id(namespace: activityNamespace, key: rawID)
        let previous = toolActivities[id]
        if previous?.state == "waiting", state == "running", permissionStates[id] == "waiting" { return }
        // Mods and stream-json can describe the same call in either order.
        // A late start/wait event must never resurrect a settled call.
        if let previous, ["completed", "error", "stopped"].contains(previous.state), ["running", "waiting"].contains(state) { return }
        if let previous, previous.state == "error", state == "completed" { return }
        let toolName = name ?? previous?.toolName ?? "Tool"
        let selectedSummary = summary ?? (input == nil ? previous?.summary : nil) ?? ActivitySupport.summary(tool: toolName, input: input)
        let terminal = ["completed", "error", "stopped"].contains(state)
        if !terminal, activityStarts[id] == nil {
            let now = activityClock()
            if now.isFinite, now >= 0 { activityStarts[id] = now }
        }
        let duration = terminal ? finishDuration(id: id, previous: previous) : nil
        guard let value = ActivitySupport.normalized(AgentActivity(id: id, provider: provider, kind: ActivitySupport.kind(tool: toolName), state: state, toolName: toolName, summary: selectedSummary, output: output ?? previous?.output, durationMs: duration)), value != previous else { return }
        if previous == nil {
            activityOrder.append(id)
            if activityOrder.count > 512 { let expired = activityOrder.removeFirst(); toolActivities.removeValue(forKey: expired); activityStarts.removeValue(forKey: expired); modSequences.removeValue(forKey: expired); permissionStates.removeValue(forKey: expired) }
        }
        toolActivities[id] = value
        if graphTracker?.activity(value, toolID: rawID) != true { activity?(value) }
    }

    private func finishDuration(id: String, previous: AgentActivity?) -> Double? {
        if let value = previous?.durationMs { return value }
        guard let started = activityStarts.removeValue(forKey: id) else { return nil }
        let milliseconds = (activityClock() - started) * 1_000
        return ActivitySupport.validDuration(milliseconds) ? milliseconds : nil
    }

    /// Consume the authenticated Mods envelope through the same identities and
    /// state machine as stdout, instead of scanning the desktop's own logs.
    public func receiveMod(_ value: ModMetadata) {
        guard provider == "claude" else { return }
        graphTracker?.receiveMod(value)
        usageTracker.consumeMod(value)
        if value.event == "session.usage" { return }
        if value.event == "turn.start" { turn("Claude 응답 생성 중"); return }
        if value.event == "turn.complete" {
            // A subagent or model turn ending is not the CLI process ending.
            if value.agentId == nil { turn("Claude 응답 마무리 중") }
            return
        }
        guard let rawID = value.toolUseId, let name = value.tool else { return }
        let id = ActivitySupport.id(namespace: activityNamespace, key: rawID)
        if let sequence = value.sequence {
            if let previous = modSequences[id], previous >= sequence { return }
            modSequences[id] = sequence
        }
        switch value.event {
        case "tool.call": tool(id: rawID, name: name, state: name == "AskUserQuestion" ? "waiting" : "running", summary: value.summary)
        case "tool.waiting":
            // The stdio approval owns this call's waiting state once observed.
            // A delayed best-effort Mod must not undo an explicit UI response.
            if permissionStates[id] == nil { tool(id: rawID, name: name, state: "waiting", summary: value.summary) }
        case "tool.complete": tool(id: rawID, name: name, state: value.isError == true ? "error" : "completed", output: value.output, summary: value.summary)
        default: break
        }
    }

    /// Missing tool results remain explicit; process exit is not proof that an
    /// individual tool succeeded. The runner emits the final turn separately.
    public func finishActivities(stopped: Bool) {
        for id in activityOrder {
            guard var value = toolActivities[id], ["running", "waiting"].contains(value.state) else { continue }
            value.state = stopped ? "stopped" : "error"
            value.output = value.output ?? "도구 결과를 받기 전에 실행이 종료되었습니다."
            value.durationMs = finishDuration(id: id, previous: value)
            toolActivities[id] = value
            if graphTracker?.activity(value) != true { activity?(value) }
        }
    }
    /// Called once after draining stdout and settling tools, before the runner
    /// publishes its terminal status. Child turns never invoke this themselves.
    public func finishGraph(state: String) { graphTracker?.finish(state: state) }
    /// A follow-up the runner wrote to Claude's stdin during this turn.
    public func steer(id: String, text: String) { graphTracker?.steer(id: id, text: text) }
    func permissionActivity(_ request: ToolPermissionRequest, state: String) {
        permissionStates[ActivitySupport.id(namespace: activityNamespace, key: request.toolUseId)] = state
        tool(id: request.toolUseId, name: request.toolName, state: state, summary: request.summary)
    }
    private func consume(_ data: Data) {
        guard !data.isEmpty else { return }
        guard let object = try? JSONSerialization.jsonObject(with: data) else { log("output", String(String(decoding: data, as: UTF8.self).prefix(32_768))); return }
        guard let value = object as? [String: Any], let type = value["type"] as? String else { return }
        let claudeChild = provider == "claude" && ExecutionGraphTracker.parentToolID(value) != nil
        if !claudeChild { usageTracker.consume(value) }
        graphTracker?.consume(value)
        switch provider {
        case "claude":
            if ["control_request", "control_response", "control_cancel_request"].contains(type) { control?(data); return }
            if !claudeChild { resumeIfValid(value["session_id"]) }
            if type == "system", value["subtype"] as? String == "permission_denied" {
                tool(id: value["tool_use_id"] as? String, name: value["tool_name"] as? String, state: "error", output: errorText(value["message"], fallback: "Claude 권한 규칙 또는 선택한 모드에서 거부했습니다."))
            } else if type == "system", value["subtype"] as? String == "compact_boundary" {
                if !claudeChild { log("system", ContextCompaction.title + " · " + ContextCompaction.claudeSummary(value["compact_metadata"])) }
            } else if type == "assistant", let message = value["message"] as? [String: Any], let blocks = message["content"] as? [[String: Any]] {
                let content = blocks.filter { $0["type"] as? String == "text" }.compactMap { $0["text"] as? String }.joined(separator: "\n")
                if !claudeChild { emitUnique(content, id: (value["uuid"] as? String) ?? (message["id"] as? String) ?? "message") }
                for block in blocks where block["type"] as? String == "tool_use" {
                    let name = block["name"] as? String
                    tool(id: block["id"] as? String, name: name, input: block["input"], state: name == "AskUserQuestion" ? "waiting" : "running")
                }
            } else if type == "user", let message = value["message"] as? [String: Any], let blocks = message["content"] as? [[String: Any]] {
                for block in blocks where block["type"] as? String == "tool_result" {
                    tool(id: block["tool_use_id"] as? String, state: block["is_error"] as? Bool == true ? "error" : "completed", output: ActivitySupport.output(block["content"]))
                }
            } else if type == "result" {
                guard !claudeChild else { return }
                if value["is_error"] as? Bool == true || (value["subtype"] as? String ?? "").hasPrefix("error") {
                    failed = true
                    let errors = (value["errors"] as? [String])?.joined(separator: "\n")
                    log("error", errors?.isEmpty == false ? errors! : errorText(value["result"], fallback: "Claude 실행 중 오류가 발생했습니다."))
                } else if !assistantSeen, let text = value["result"] as? String { emitUnique(text, id: "result") }
                turn("Claude 응답 마무리 중")
                result?()
            }
        case "codex":
            if type == "thread.started" { resumeIfValid(value["thread_id"]) }
            if type == "turn.started" { turn("Codex 응답 생성 중") }
            if type == "turn.completed" { turn("Codex 응답 마무리 중") }
            if ["item.started", "item.updated", "item.completed"].contains(type), let item = value["item"] as? [String: Any], let itemType = item["type"] as? String {
                let ended = type == "item.completed"
                let state = item["status"] as? String == "failed" ? "error" : ended ? "completed" : "running"
                switch itemType {
                case "agent_message": if ended, let text = item["text"] as? String { emitUnique(text, id: item["id"] as? String ?? "message") }
                case "command_execution":
                    let output = item["aggregated_output"] as? String
                    tool(id: item["id"] as? String, name: "command_execution", input: item, state: state, output: ended ? output : nil)
                    if activity == nil, ended, let output, !output.isEmpty { log("output", output) }
                case "file_change": tool(id: item["id"] as? String, name: "file_change", input: item, state: state)
                case "web_search": tool(id: item["id"] as? String, name: "web_search", input: item, state: state)
                case "context_compaction": if ended { log("system", ContextCompaction.title + " · " + ContextCompaction.codexSummary) }
                case "collab_tool_call", "collab_agent_tool_call":
                    if let collaboration = CodexCollaborationItem(item) {
                        tool(id: collaboration.id, name: collaboration.tool, state: state,
                             output: collaboration.output, summary: collaboration.summary)
                    }
                case "mcp_tool_call":
                    let name = [item["server"] as? String, item["tool"] as? String].compactMap { $0 }.joined(separator: ".")
                    tool(id: item["id"] as? String, name: name.isEmpty ? "MCP" : name, input: item["arguments"], state: state, output: ActivitySupport.output(item["error"] ?? item["result"]))
                default: break // Reasoning text is deliberately not copied.
                }
            }
            if type == "turn.failed" || type == "error" { failed = true; log("error", errorText(value["error"] ?? value["message"], fallback: "Codex 실행 중 오류가 발생했습니다.")) }
        case "gemini":
            if type == "init" { resumeIfValid(value["session_id"]); turn("Gemini 응답 생성 중") }
            if type == "message", value["role"] as? String == "assistant", let text = value["content"] as? String {
                if value["delta"] as? Bool == true {
                    let remaining = max(0, 131_072 - pendingText.utf8.count)
                    if text.utf8.count > remaining { pendingTextTruncated = true }
                    pendingText += ActivitySupport.prefixUTF8(text, maximumBytes: remaining)
                }
                else { flushText(); if !text.isEmpty { log("assistant", text) } }
            } else if type == "tool_use" {
                flushText()
                if let name = value["tool_name"] as? String {
                    tool(id: value["tool_id"] as? String, name: name, input: value["parameters"], state: "running")
                    if activity == nil { log("system", "도구 실행 · \(name.prefix(160))") }
                }
            } else if type == "tool_result" {
                flushText()
                let failed = value["status"] as? String == "error"
                tool(id: value["tool_id"] as? String, state: failed ? "error" : "completed", output: failed ? errorText(value["error"], fallback: "Gemini 도구 실행이 실패했습니다.") : value["output"] as? String)
                if activity == nil {
                    if let text = value["output"] as? String, !text.isEmpty { log("output", text) }
                    if failed { log("system", errorText(value["error"], fallback: "Gemini 도구 실행이 실패했습니다.")) }
                }
            } else if type == "error" {
                flushText(); let warning = value["severity"] as? String == "warning"; if !warning { failed = true }
                log(warning ? "system" : "error", errorText(value["message"], fallback: "Gemini 실행 중 오류가 발생했습니다."))
            } else if type == "result" {
                flushText(); if value["status"] as? String == "error" { failed = true; log("error", errorText(value["error"], fallback: "Gemini 실행 중 오류가 발생했습니다.")) }
                turn("Gemini 응답 마무리 중")
            }
        default: break
        }
    }
}

/// Preserve a UTF-8 character split across two reads from a shell pipe.
final class UTF8StreamDecoder {
    private var pending = Data()
    func push(_ data: Data) -> String {
        pending.append(data)
        if let text = String(data: pending, encoding: .utf8) { pending.removeAll(keepingCapacity: true); return text }
        for trailing in 1...min(3, pending.count) {
            if let text = String(data: pending.dropLast(trailing), encoding: .utf8) { pending = Data(pending.suffix(trailing)); return text }
        }
        let text = String(decoding: pending, as: UTF8.self); pending.removeAll(); return text
    }
    func flush() -> String { defer { pending.removeAll() }; return String(decoding: pending, as: UTF8.self) }
}
