import CryptoKit
import Foundation

/// Tokens the engine reported for one block. Each assistant message carries the
/// usage of its own API call; a block's figure is the sum over its messages.
public struct GraphTokenUsage: Codable, Sendable, Equatable {
    public var inputTokens: Int
    public var outputTokens: Int
    public var cacheReadTokens: Int
    public var cacheCreationTokens: Int
    public static let maximumTokens = 1_000_000_000_000

    public init(inputTokens: Int = 0, outputTokens: Int = 0, cacheReadTokens: Int = 0, cacheCreationTokens: Int = 0) {
        self.inputTokens = inputTokens; self.outputTokens = outputTokens
        self.cacheReadTokens = cacheReadTokens; self.cacheCreationTokens = cacheCreationTokens
    }
    public var total: Int { inputTokens + outputTokens + cacheReadTokens + cacheCreationTokens }
    public var isEmpty: Bool { total == 0 }
    public var normalized: GraphTokenUsage? {
        let values = [inputTokens, outputTokens, cacheReadTokens, cacheCreationTokens]
        guard values.allSatisfy({ $0 >= 0 && $0 <= Self.maximumTokens }), total > 0 else { return nil }
        return self
    }
    public static func + (lhs: GraphTokenUsage, rhs: GraphTokenUsage) -> GraphTokenUsage {
        GraphTokenUsage(inputTokens: lhs.inputTokens + rhs.inputTokens, outputTokens: lhs.outputTokens + rhs.outputTokens,
                        cacheReadTokens: lhs.cacheReadTokens + rhs.cacheReadTokens, cacheCreationTokens: lhs.cacheCreationTokens + rhs.cacheCreationTokens)
    }
    public static func - (lhs: GraphTokenUsage, rhs: GraphTokenUsage) -> GraphTokenUsage {
        GraphTokenUsage(inputTokens: max(0, lhs.inputTokens - rhs.inputTokens), outputTokens: max(0, lhs.outputTokens - rhs.outputTokens),
                        cacheReadTokens: max(0, lhs.cacheReadTokens - rhs.cacheReadTokens), cacheCreationTokens: max(0, lhs.cacheCreationTokens - rhs.cacheCreationTokens))
    }

    /// The `usage` object of a CLI stream message. Missing counters are zero;
    /// a missing or empty object is no observation at all.
    public static func parse(_ value: Any?) -> GraphTokenUsage? {
        guard let object = value as? [String: Any] else { return nil }
        func count(_ key: String) -> Int? {
            if let number = object[key] as? Int { return number }
            if let number = object[key] as? Double, number.isFinite, number >= 0, number <= Double(maximumTokens) { return Int(number) }
            return nil
        }
        let usage = GraphTokenUsage(inputTokens: count("input_tokens") ?? 0, outputTokens: count("output_tokens") ?? 0,
                                    cacheReadTokens: count("cache_read_input_tokens") ?? 0, cacheCreationTokens: count("cache_creation_input_tokens") ?? 0)
        return usage.normalized
    }

    public static func compact(_ tokens: Int) -> String {
        if tokens < 1_000 { return String(tokens) }
        if tokens < 1_000_000 { return String(format: tokens < 10_000 ? "%.1fK" : "%.0fK", Double(tokens) / 1_000) }
        return String(format: tokens < 10_000_000 ? "%.2fM" : "%.1fM", Double(tokens) / 1_000_000)
    }
    public var summary: String { "토큰 " + Self.compact(total) }
    public var detail: String {
        var parts = ["입력 " + Self.compact(inputTokens), "출력 " + Self.compact(outputTokens)]
        if cacheReadTokens > 0 { parts.append("캐시 읽기 " + Self.compact(cacheReadTokens)) }
        if cacheCreationTokens > 0 { parts.append("캐시 생성 " + Self.compact(cacheCreationTokens)) }
        return parts.joined(separator: " · ") + " · 합계 " + Self.compact(total)
    }
}

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
    public var usage: GraphTokenUsage?
    /// Explicit new work on the same agent; absent in older graph events.
    public var activityGeneration: Int?

    public init(id: String, runId: String, parentId: String? = nil, kind: String, state: String, title: String,
                input: String? = nil, output: String? = nil, entries: [LogEntry] = [], updatedAt: String = mightyTimestamp(), usage: GraphTokenUsage? = nil, activityGeneration: Int? = nil) {
        self.id = id; self.runId = runId; self.parentId = parentId; self.kind = kind; self.state = state
        self.title = title; self.input = input; self.output = output; self.entries = entries; self.updatedAt = updatedAt; self.usage = usage; self.activityGeneration = activityGeneration
    }
}

public enum ExecutionGraphSupport {
    public static let maximumActivityGeneration = 1_000_000
    public static func normalizedGeneration(_ value: Int?) -> Int? {
        value.flatMap { (0...maximumActivityGeneration).contains($0) ? $0 : nil }
    }
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
              ["main", "agent", "task", "steer"].contains(node.kind), ActivitySupport.states.contains(node.state),
              node.updatedAt.utf8.count <= 80, AgentRunTiming.parseTimestamp(node.updatedAt) != nil else { return nil }
        var result = node
        if result.kind == "main" { result.parentId = nil }
        result.title = ActivitySupport.clean(node.title, maximumBytes: 160, singleLine: true)
        result.input = node.input.map { ActivitySupport.clean($0, maximumBytes: maximumInputBytes) }
        result.output = node.output.map { ActivitySupport.clean($0, maximumBytes: maximumOutputBytes) }
        result.usage = node.usage?.normalized
        result.activityGeneration = normalizedGeneration(node.activityGeneration)
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
