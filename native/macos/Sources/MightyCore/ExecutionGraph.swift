import CryptoKit
import Foundation

/// A request-scoped node snapshot. Missing input/output means the engine did
/// not report it; display code must not manufacture a prompt or final answer.
public struct ExecutionGraphNode: Codable, Sendable, Equatable, Identifiable {
    public var id: String
    public var runId: String
    public var parentId: String?
    public var kind: String
    public var state: String
    public var title: String
    public var input: String?
    public var output: String?
    public var entries: [LogEntry]
    public var updatedAt: String

    public init(id: String, runId: String, parentId: String? = nil, kind: String, state: String, title: String,
                input: String? = nil, output: String? = nil, entries: [LogEntry] = [], updatedAt: String = mightyTimestamp()) {
        self.id = id; self.runId = runId; self.parentId = parentId; self.kind = kind; self.state = state
        self.title = title; self.input = input; self.output = output; self.entries = entries; self.updatedAt = updatedAt
    }
}

public enum ExecutionGraphSupport {
    public static let maximumNodes = 128
    public static let maximumEntries = 80
    public static let maximumInputBytes = 16 * 1024
    public static let maximumOutputBytes = 32 * 1024
    public static let maximumNodeBytes = 64 * 1024

    public static func mainNodeID(runId: String) -> String { identifier(runId, "main") }
    public static func agentNodeID(runId: String, toolUseId: String) -> String { identifier(runId, "tool:" + toolUseId) }

    static func identifier(_ runId: String, _ key: String) -> String {
        "graph-" + SHA256.hash(data: Data((runId + "|" + key).utf8)).map { String(format: "%02x", $0) }.joined().prefix(48)
    }
    static func terminal(_ state: String) -> Bool { ["completed", "error", "stopped"].contains(state) }

    /// Shared by the authenticated wire boundary and persisted graph history.
    /// The byte budget includes prompts, final output, and all entry metadata.
    public static func normalized(_ node: ExecutionGraphNode, restoring: Bool = false) -> ExecutionGraphNode? {
        guard CoreValidation.identifier(node.id), CoreValidation.identifier(node.runId),
              node.parentId.map({ CoreValidation.identifier($0) && $0 != node.id }) ?? true,
              ["main", "agent"].contains(node.kind), ActivitySupport.states.contains(node.state),
              node.updatedAt.utf8.count <= 80, AgentRunTiming.parseTimestamp(node.updatedAt) != nil else { return nil }
        var result = node
        if result.kind == "main" { result.parentId = nil }
        result.title = ActivitySupport.clean(node.title, maximumBytes: 160, singleLine: true)
        result.input = node.input.map { ActivitySupport.clean($0, maximumBytes: maximumInputBytes) }
        result.output = node.output.map { ActivitySupport.clean($0, maximumBytes: maximumOutputBytes) }
        if restoring && !terminal(result.state) { result.state = "stopped" }
        if node.kind == "main" { result.entries = []; return result }

        var remaining = maximumNodeBytes - result.title.utf8.count - (result.input?.utf8.count ?? 0) - (result.output?.utf8.count ?? 0)
        var seen = Set<String>()
        var entries: [LogEntry] = []
        for var entry in node.entries.suffix(maximumEntries).reversed() {
            guard CoreValidation.identifier(entry.id), seen.insert(entry.id).inserted,
                  ["user", "assistant", "system", "output", "error"].contains(entry.kind),
                  entry.provider.map(ProviderOptions.ids.contains) ?? true,
                  entry.timestamp.utf8.count <= 80, AgentRunTiming.parseTimestamp(entry.timestamp) != nil,
                  remaining > 256 else { continue }
            entry.activity = entry.activity.flatMap { ActivitySupport.normalized($0, restoring: restoring) }
            let metadata = entry.id.utf8.count + entry.timestamp.utf8.count + entry.kind.utf8.count + (entry.provider?.utf8.count ?? 0)
            var activityBytes = entry.activity.map { $0.id.utf8.count + $0.kind.utf8.count + $0.state.utf8.count + $0.provider.utf8.count + $0.summary.utf8.count + ($0.toolName?.utf8.count ?? 0) + ($0.output?.utf8.count ?? 0) } ?? 0
            if metadata + activityBytes >= remaining { entry.activity = nil; activityBytes = 0 }
            let available = max(0, remaining - metadata - activityBytes)
            entry.text = ActivitySupport.clean(entry.text, maximumBytes: min(maximumOutputBytes, available))
            remaining -= metadata + activityBytes + entry.text.utf8.count
            entries.append(entry)
        }
        result.entries = Array(entries.reversed())
        return result
    }
}
