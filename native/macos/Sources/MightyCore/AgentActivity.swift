import Foundation
import CryptoKit

public enum ActivitySupport {
    public static let kinds = ["turn", "tool", "command", "read", "edit", "search", "web", "agent"]
    public static let states = ["running", "waiting", "completed", "error", "stopped"]
    public static let maximumSummaryBytes = 1_000
    public static let maximumOutputBytes = 8_192
    public static let maximumDurationMs: Double = 30 * 24 * 60 * 60 * 1_000

    public static func validDuration(_ milliseconds: Double) -> Bool {
        milliseconds.isFinite && milliseconds >= 0 && milliseconds <= maximumDurationMs
    }

    public static func durationLabel(_ activity: AgentActivity) -> String? {
        guard activity.kind != "turn", ["completed", "error", "stopped"].contains(activity.state),
              let milliseconds = activity.durationMs, validDuration(milliseconds) else { return nil }
        if milliseconds < 1 { return milliseconds == 0 ? "0ms" : "<1ms" }
        if milliseconds < 1_000 { return "\(Int(milliseconds))ms" }
        if milliseconds < 60_000 { return String(format: "%.1f초", floor(milliseconds / 100) / 10) }
        let seconds = Int(milliseconds / 1_000)
        return seconds % 60 == 0 ? "\(seconds / 60)분" : "\(seconds / 60)분 \(seconds % 60)초"
    }

    static func prefixUTF8(_ value: String, maximumBytes: Int) -> String {
        guard value.utf8.count > maximumBytes else { return value }
        let prefix = Data(value.utf8.prefix(max(0, maximumBytes)))
        for dropped in 0...min(3, prefix.count) {
            if let text = String(data: prefix.dropLast(dropped), encoding: .utf8) { return text }
        }
        return ""
    }

    /// Bound UTF-8 payloads and remove terminal controls before UI/persistence.
    public static func clean(_ value: String, maximumBytes: Int, singleLine: Bool = false) -> String {
        let plain = value.replacingOccurrences(of: "\\x1b(?:\\[[0-?]*[ -/]*[@-~]|\\][^\\x07]*(?:\\x07|\\x1b\\\\))", with: "", options: .regularExpression)
        var result = String.UnicodeScalarView(); var bytes = 0
        for scalar in plain.unicodeScalars {
            guard scalar.value == 9 || scalar.value == 10 || scalar.value == 13 || scalar.properties.generalCategory != .control else { continue }
            let item: Unicode.Scalar = singleLine && CharacterSet.whitespacesAndNewlines.contains(scalar) ? " " : scalar
            let count = item.utf8.count
            guard bytes + count <= maximumBytes else { break }
            result.append(item); bytes += count
        }
        return String(result).trimmingCharacters(in: .whitespacesAndNewlines)
    }

    public static func normalized(_ activity: AgentActivity, restoring: Bool = false) -> AgentActivity? {
        guard CoreValidation.identifier(activity.id), ProviderOptions.ids.contains(activity.provider), kinds.contains(activity.kind), states.contains(activity.state) else { return nil }
        var result = activity
        result.summary = clean(result.summary, maximumBytes: maximumSummaryBytes, singleLine: true)
        if let tool = result.toolName { result.toolName = clean(tool, maximumBytes: 160, singleLine: true) }
        if let output = result.output { result.output = clean(output, maximumBytes: maximumOutputBytes) }
        if result.output?.isEmpty == true { result.output = nil }
        if result.toolName?.isEmpty == true { result.toolName = nil }
        if let duration = result.durationMs,
           !validDuration(duration) || result.kind == "turn" || !["completed", "error", "stopped"].contains(result.state) { result.durationMs = nil }
        if restoring && ["running", "waiting"].contains(result.state) { result.state = "stopped" }
        return result
    }

    public static func valid(_ activity: AgentActivity) -> Bool { normalized(activity) == activity }

    static func id(namespace: String, key: String) -> String {
        "activity-" + SHA256.hash(data: Data((namespace + "|" + key).utf8)).map { String(format: "%02x", $0) }.joined().prefix(48)
    }

    static func kind(tool: String) -> String {
        let name = tool.lowercased()
        if ["bash", "run_shell_command", "command_execution", "exec_command", "shell"].contains(name) { return "command" }
        if ["read", "read_file", "read_many_files", "list_directory", "ls"].contains(name) { return "read" }
        if ["edit", "write", "write_file", "replace", "apply_patch", "file_change", "notebookedit"].contains(name) { return "edit" }
        if ["grep", "glob", "search_file_content", "glob_search"].contains(name) { return "search" }
        if ["websearch", "webfetch", "web_search", "google_web_search", "web_fetch"].contains(name) { return "web" }
        if ["agent", "task", "spawn_agent", "send_input", "wait", "close_agent", "delegate_to_agent"].contains(name) { return "agent" }
        return "tool"
    }

    /// Only known display fields are selected. Prompts, source-file contents,
    /// arbitrary MCP arguments, reasoning, credentials and images are not dumped.
    static func summary(tool: String, input: Any?) -> String {
        var fields = input as? [String: Any]
        if let raw = input as? String, raw.utf8.count <= 65_536, let data = raw.data(using: .utf8) { fields = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] }
        for key in ["command", "file_path", "absolute_path", "path", "pattern", "query", "url", "description", "target_file", "filename", "glob"] {
            if let value = fields?[key] as? String, !value.isEmpty { return clean(value, maximumBytes: maximumSummaryBytes, singleLine: true) }
            if let values = fields?[key] as? [String], !values.isEmpty { return clean(values.prefix(8).joined(separator: " "), maximumBytes: maximumSummaryBytes, singleLine: true) }
        }
        if let changes = fields?["changes"] as? [[String: Any]] {
            let paths = changes.prefix(12).compactMap { change -> String? in
                guard let path = change["path"] as? String else { return nil }
                return (change["kind"] as? String).map { $0 + " " + path } ?? path
            }
            if !paths.isEmpty { return clean(paths.joined(separator: " · "), maximumBytes: maximumSummaryBytes, singleLine: true) }
        }
        return clean(tool, maximumBytes: maximumSummaryBytes, singleLine: true)
    }

    static func output(_ value: Any?) -> String? {
        if let text = value as? String { return clean(text, maximumBytes: maximumOutputBytes) }
        if let blocks = value as? [[String: Any]] {
            return clean(blocks.prefix(32).filter { $0["type"] as? String == "text" }.compactMap { $0["text"] as? String }.joined(separator: "\n"), maximumBytes: maximumOutputBytes)
        }
        guard let record = value as? [String: Any] else { return nil }
        if let text = record["message"] as? String { return clean(text, maximumBytes: maximumOutputBytes) }
        if let content = record["content"] { return output(content) }
        return nil
    }
}

/// The public `codex exec --json` collaboration item, not app-server's
/// camelCase schema. Select only its documented display fields.
struct CodexCollaborationItem {
    struct AgentState {
        let status: String
        let message: String?
    }
    let id: String
    let tool: String
    let sender: String?
    let receivers: [String]
    let states: [String: AgentState]
    let prompt: String?
    let status: String

    static func key(_ value: Any?) -> String? {
        guard let value = value as? String, !value.isEmpty, value.utf8.count <= 512,
              !value.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) }) else { return nil }
        return value
    }
    init?(_ item: [String: Any]) {
        guard ["collab_tool_call", "collab_agent_tool_call"].contains(item["type"] as? String ?? ""),
              let id = Self.key(item["id"]), let tool = item["tool"] as? String,
              ["spawn_agent", "send_input", "wait", "close_agent"].contains(tool) else { return nil }
        self.id = id; self.tool = tool; sender = Self.key(item["sender_thread_id"])
        var seen = Set<String>()
        receivers = (item["receiver_thread_ids"] as? [String] ?? []).prefix(ExecutionGraphSupport.maximumNodes)
            .compactMap(Self.key).filter { seen.insert($0).inserted }
        var states: [String: AgentState] = [:]
        for (thread, raw) in (item["agents_states"] as? [String: Any] ?? [:]).sorted(by: { $0.key < $1.key }).prefix(ExecutionGraphSupport.maximumNodes) {
            guard let thread = Self.key(thread), let raw = raw as? [String: Any], let status = raw["status"] as? String else { continue }
            states[thread] = AgentState(status: String(status.prefix(80)), message: (raw["message"] as? String).map { ActivitySupport.clean($0, maximumBytes: ExecutionGraphSupport.maximumOutputBytes) })
        }
        self.states = states
        prompt = (item["prompt"] as? String).map { ActivitySupport.clean($0, maximumBytes: ExecutionGraphSupport.maximumInputBytes) }.flatMap { $0.isEmpty ? nil : $0 }
        status = item["status"] as? String ?? "in_progress"
    }
    var threads: [String] { Array((receivers + states.keys.sorted().filter { !receivers.contains($0) }).prefix(ExecutionGraphSupport.maximumNodes)) }
    var summary: String {
        let action: String
        switch tool {
        case "spawn_agent": action = "하위 에이전트 생성"
        case "send_input": action = "하위 에이전트에 지시"
        case "wait": action = "하위 에이전트 응답 대기"
        default: action = "하위 에이전트 종료"
        }
        let targets = threads.prefix(4).map { String($0.prefix(12)) }.joined(separator: ", ")
        return ActivitySupport.clean([action, prompt, targets.isEmpty ? nil : targets].compactMap { $0 }.joined(separator: " · "), maximumBytes: ActivitySupport.maximumSummaryBytes, singleLine: true)
    }
    var output: String? {
        let rows = threads.compactMap { thread -> String? in
            guard let state = states[thread] else { return nil }
            let label = "\(thread.prefix(12)) · \(state.status)"
            return state.message.map { label + "\n" + $0 } ?? label
        }
        return rows.isEmpty ? nil : ActivitySupport.clean(rows.joined(separator: "\n\n"), maximumBytes: ActivitySupport.maximumOutputBytes)
    }
}
