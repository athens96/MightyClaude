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
    public var summary: String { L("graph.usage.summary", ["total": Self.compact(total)]) }
    public var detail: String {
        var parts = [L("graph.usage.input", ["value": Self.compact(inputTokens)]), L("graph.usage.output", ["value": Self.compact(outputTokens)])]
        if cacheReadTokens > 0 { parts.append(L("graph.usage.cacheRead", ["value": Self.compact(cacheReadTokens)])) }
        if cacheCreationTokens > 0 { parts.append(L("graph.usage.cacheCreation", ["value": Self.compact(cacheCreationTokens)])) }
        return parts.joined(separator: " · ") + " · " + L("graph.usage.total", ["value": Self.compact(total)])
    }
}

/// Per-response attribution record for a graph node. Carries the model that
/// produced the response, the tokens it used, and the tool_use IDs it called.
public struct GraphResponseRecord: Codable, Sendable, Equatable {
    public var responseId: String
    /// The model the CLI reported for this response; nil when the message
    /// carried no "model" field (e.g. Codex turn items).
    public var model: String?
    public var usage: GraphTokenUsage
    /// tool_use block IDs from this response's content, in order.
    public var activityIds: [String]
    /// True when the model comes from the run's configured default rather than
    /// a real CLI report (Codex turn-completed items).
    public var markedAsConfigured: Bool

    public init(responseId: String, model: String? = nil, usage: GraphTokenUsage, activityIds: [String] = [], markedAsConfigured: Bool = false) {
        self.responseId = responseId; self.model = model; self.usage = usage
        self.activityIds = activityIds; self.markedAsConfigured = markedAsConfigured
    }
}

/// Who a sub-agent block is, as the CLI said it: the Agent call's `subagent_type`, `model` and `isolation`, and the
/// model the CLI resolved when it launched the agent. Every part is optional; a block without any carries none
/// (older graph events and saves, Codex and Antigravity sub-agents). `FlowRoles` reads it for the flow diagram.
public struct GraphAgentIdentity: Codable, Sendable, Equatable {
    /// The Agent call's `subagent_type` (Explore, general-purpose, a custom or plugin agent's name, `fork`), else the
    /// type the CLI named when it started the agent (`task_started`, Mods' `agent.spawn`, the result's `agentType`).
    public var type: String?
    /// The Agent call's `model`: what was asked for (an alias such as `haiku`), not what ran.
    public var requestedModel: String?
    /// The model the CLI said the agent runs with (the launch result's `resolvedModel`, Mods' spawn result).
    public var resolvedModel: String?
    /// The Agent call's `isolation`: `worktree` (a checkout of its own) or `remote`.
    public var isolation: String?

    public init(type: String? = nil, requestedModel: String? = nil, resolvedModel: String? = nil, isolation: String? = nil) {
        self.type = type; self.requestedModel = requestedModel; self.resolvedModel = resolvedModel; self.isolation = isolation
    }

    /// The Agent call's type for a fork of the calling session.
    public static let forkType = "fork"
    public static let isolations = ["worktree", "remote"]
    /// A fork inherits its caller's whole conversation and model.
    public var isFork: Bool { type == Self.forkType }
    public var isEmpty: Bool { type == nil && requestedModel == nil && resolvedModel == nil && isolation == nil }

    /// Bounded for the wire and for saves: a type on one line, model names the CLI could name, a known isolation.
    /// Nil when nothing is left.
    public var normalized: GraphAgentIdentity? {
        func model(_ value: String?) -> String? { value.flatMap { CoreValidation.model($0) ? $0 : nil } }
        let type = self.type.map { ActivitySupport.clean($0, maximumBytes: 160, singleLine: true) }.flatMap { $0.isEmpty ? nil : $0 }
        let value = GraphAgentIdentity(type: type, requestedModel: model(requestedModel), resolvedModel: model(resolvedModel),
                                       isolation: isolation.flatMap { Self.isolations.contains($0) ? $0 : nil })
        return value.isEmpty ? nil : value
    }
    var byteCount: Int { (type?.utf8.count ?? 0) + (requestedModel?.utf8.count ?? 0) + (resolvedModel?.utf8.count ?? 0) + (isolation?.utf8.count ?? 0) }
}

/// The figures a sub-agent block keeps of its own run, for the flow diagram's compact card (`FlowCard`): when the
/// app saw it start and end, and how many tools it called so far. Each is what was observed, never an estimate; a
/// block without any carries none (older events and saves, Codex and Antigravity sub-agents).
public struct GraphAgentFigures: Codable, Sendable, Equatable {
    /// When the Agent call that started it was seen: that frame's own `timestamp`, else the moment the app read it.
    public var startedAt: String?
    /// When it was seen to end (its result, its notification, the process's end), by the same clock.
    public var endedAt: String?
    /// Tool calls of its own so far: the tool_use blocks of its messages, or the count the CLI reports when that is more.
    public var toolCalls: Int?

    public init(startedAt: String? = nil, endedAt: String? = nil, toolCalls: Int? = nil) {
        self.startedAt = startedAt; self.endedAt = endedAt; self.toolCalls = toolCalls
    }
    public static let maximumToolCalls = 1_000_000
    public var isEmpty: Bool { startedAt == nil && endedAt == nil && toolCalls == nil }
    /// A timestamp the app can read back, a count in range; nil when nothing is left.
    public var normalized: GraphAgentFigures? {
        func time(_ value: String?) -> String? {
            value.flatMap { $0.utf8.count <= 80 && AgentRunTiming.parseTimestamp($0) != nil ? $0 : nil }
        }
        let value = GraphAgentFigures(startedAt: time(startedAt), endedAt: time(endedAt),
                                      toolCalls: toolCalls.flatMap { (0...Self.maximumToolCalls).contains($0) ? $0 : nil })
        return value.isEmpty ? nil : value
    }
    public var started: Date? { startedAt.flatMap(AgentRunTiming.parseTimestamp) }
    public var ended: Date? { endedAt.flatMap(AgentRunTiming.parseTimestamp) }
}

/// One consultation of the advisor (Claude Code's server-side advisor tool, `claude --advisor <model>`), as the
/// stream showed it and nothing more: the advisor's reply usually goes to Claude encrypted, and then there is no text
/// here to show.
public struct GraphAdvisorCall: Codable, Sendable, Equatable, Identifiable {
    /// The server tool call's id (`srvtoolu_…`).
    public var id: String
    /// The block that asked: a sub-agent block's id; nil is the run's main session.
    public var caller: String?
    /// When the call was seen and when its result was: the frames' own `timestamp`, else the moment they were read.
    public var startedAt: String?
    public var endedAt: String?
    /// `consulting` until its result; then `done` (a reply came), `declined` (the reply was a refusal), `error` (the
    /// CLI reported an error: `errorCode`) or `stopped` (the turn or the process ended before a result).
    public var state: String
    /// The reply came encrypted (`advisor_redacted_result`): it went to Claude and there is no text to show.
    public var redacted: Bool?
    /// The advice, when the CLI gave it as text (`advisor_result`), bounded.
    public var text: String?
    /// An error result's `error_code`.
    public var errorCode: String?
    /// When it asked, the caller had called no file-changing tool yet (Edit, Write, MultiEdit, NotebookEdit).
    public var beforeChange: Bool?
    /// The caller called a tool again after it (another consultation counts): it was not the caller's last step.
    public var followed: Bool?

    public init(id: String, caller: String? = nil, startedAt: String? = nil, endedAt: String? = nil, state: String = "consulting", redacted: Bool? = nil,
                text: String? = nil, errorCode: String? = nil, beforeChange: Bool? = nil, followed: Bool? = nil) {
        self.id = id; self.caller = caller; self.startedAt = startedAt; self.endedAt = endedAt; self.state = state; self.redacted = redacted
        self.text = text; self.errorCode = errorCode; self.beforeChange = beforeChange; self.followed = followed
    }
    public var started: Date? { startedAt.flatMap(AgentRunTiming.parseTimestamp) }
    public var ended: Date? { endedAt.flatMap(AgentRunTiming.parseTimestamp) }
    var byteCount: Int { id.utf8.count + (caller?.utf8.count ?? 0) + (startedAt?.utf8.count ?? 0) + (endedAt?.utf8.count ?? 0) + state.utf8.count + (text?.utf8.count ?? 0) + (errorCode?.utf8.count ?? 0) }
}

/// A run's use of the advisor: the consultations the stream showed and what its turns' results said the advisor's own
/// messages cost. Kept on the run's main block (a notice turn's own run for what its main session asked); a run that
/// never consulted it carries none, as do older graph events and saves.
public struct GraphAdvisor: Codable, Sendable, Equatable {
    /// The advisor's model as a turn's result named it (`usage.iterations[]` of type `advisor_message`).
    public var model: String?
    /// The tokens of the advisor's own messages, summed over the results of the run's turns.
    public var usage: GraphTokenUsage?
    /// The newest consultations, oldest first, at most `maximumCalls`.
    public var calls: [GraphAdvisorCall]
    /// Every consultation seen, when that is more than `calls` keeps.
    public var total: Int?

    public init(model: String? = nil, usage: GraphTokenUsage? = nil, calls: [GraphAdvisorCall] = [], total: Int? = nil) {
        self.model = model; self.usage = usage; self.calls = calls; self.total = total
    }
    public static let maximumCalls = 32
    /// With `maximumCalls`, a run's advice never takes more than its result may.
    public static let maximumTextBytes = 1024
    public static let maximumTotal = 1_000_000
    public static let states = ["consulting", "done", "declined", "error", "stopped"]
    /// Every consultation seen, kept or not.
    public var count: Int { max(total ?? 0, calls.count) }
    public var isEmpty: Bool { model == nil && usage == nil && calls.isEmpty }
    var byteCount: Int { (model?.utf8.count ?? 0) + calls.reduce(0) { $0 + $1.byteCount } }

    /// Bounded for the wire and for saves: the newest calls with ids, states and times the app can read back and a
    /// bounded text; nil when nothing is left. `restoring`: a consultation still out when the app closed never got
    /// its result.
    public func normalized(restoring: Bool = false) -> GraphAdvisor? {
        func time(_ value: String?) -> String? {
            value.flatMap { $0.utf8.count <= 80 && AgentRunTiming.parseTimestamp($0) != nil ? $0 : nil }
        }
        var seen = Set<String>()
        var kept: [GraphAdvisorCall] = []
        for call in calls.suffix(Self.maximumCalls) {
            guard CoreValidation.identifier(call.id), Self.states.contains(call.state), call.caller.map(CoreValidation.identifier) ?? true,
                  seen.insert(call.id).inserted else { continue }
            var value = call
            value.startedAt = time(call.startedAt); value.endedAt = time(call.endedAt)
            if restoring, value.state == "consulting" { value.state = "stopped" }
            value.redacted = call.redacted == true ? true : nil
            value.text = call.text.map { ActivitySupport.clean($0, maximumBytes: Self.maximumTextBytes) }.flatMap { $0.isEmpty ? nil : $0 }
            value.errorCode = call.errorCode.map { ActivitySupport.clean($0, maximumBytes: 80, singleLine: true) }.flatMap { $0.isEmpty ? nil : $0 }
            value.beforeChange = call.beforeChange == true ? true : nil
            value.followed = call.followed == true ? true : nil
            kept.append(value)
        }
        let all = min(Self.maximumTotal, count)
        let value = GraphAdvisor(model: model.flatMap { CoreValidation.model($0) ? $0 : nil }, usage: usage?.normalized, calls: kept, total: all > kept.count ? all : nil)
        return value.isEmpty ? nil : value
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
    /// Per-response attribution; absent in older graph events and graph saves.
    public var responseRecords: [GraphResponseRecord]?
    /// Started with `run_in_background` (a background Agent; every task block); absent otherwise and in older saves.
    public var background: Bool?
    /// Where a task block's run writes its output. Never shown and never sent to the phone.
    public var outputFile: String?
    /// Claude's answer in the turn this block's notice opened, when it was that turn's only notice.
    public var noticeReply: String?
    /// A copy in a notice run (`BackgroundNoticeRuns`): the id of the finished background block it copies.
    public var sourceBlockId: String?
    /// Who a sub-agent block is (`GraphAgentIdentity`); absent on other blocks, in older graph events and in older saves.
    public var identity: GraphAgentIdentity?
    /// A sub-agent block's start, end and tool calls (`GraphAgentFigures`); absent as `identity` is.
    public var figures: GraphAgentFigures?
    /// A run's use of the advisor (`GraphAdvisor`), on its main block; absent on other blocks, in a run that never
    /// consulted it, in older graph events and in older saves.
    public var advisor: GraphAdvisor?

    public init(id: String, runId: String, parentId: String? = nil, kind: String, state: String, title: String,
                input: String? = nil, output: String? = nil, entries: [LogEntry] = [], updatedAt: String = mightyTimestamp(), usage: GraphTokenUsage? = nil, activityGeneration: Int? = nil, responseRecords: [GraphResponseRecord]? = nil,
                background: Bool? = nil, outputFile: String? = nil, noticeReply: String? = nil, sourceBlockId: String? = nil, identity: GraphAgentIdentity? = nil,
                figures: GraphAgentFigures? = nil, advisor: GraphAdvisor? = nil) {
        self.advisor = advisor
        self.id = id; self.runId = runId; self.parentId = parentId; self.kind = kind; self.state = state
        self.title = title; self.input = input; self.output = output; self.entries = entries; self.updatedAt = updatedAt; self.usage = usage; self.activityGeneration = activityGeneration; self.responseRecords = responseRecords
        self.background = background; self.outputFile = outputFile; self.noticeReply = noticeReply; self.sourceBlockId = sourceBlockId
        self.identity = identity; self.figures = figures
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
              ["main", "agent", "task", "steer", "compact", "question"].contains(node.kind), ActivitySupport.states.contains(node.state),
              node.updatedAt.utf8.count <= 80, AgentRunTiming.parseTimestamp(node.updatedAt) != nil else { return nil }
        var result = node
        if result.kind == "main" { result.parentId = nil }
        result.title = ActivitySupport.clean(node.title, maximumBytes: 160, singleLine: true)
        result.input = node.input.map { ActivitySupport.clean($0, maximumBytes: maximumInputBytes) }
        result.output = node.output.map { ActivitySupport.clean($0, maximumBytes: maximumOutputBytes) }
        result.usage = node.usage?.normalized
        result.activityGeneration = normalizedGeneration(node.activityGeneration)
        result.responseRecords = node.responseRecords
        result.background = node.background == true ? true : nil
        result.outputFile = node.outputFile.flatMap { $0.utf8.count <= 4096 && !$0.contains(where: \.isNewline) ? $0 : nil }
        result.noticeReply = node.noticeReply.map { ActivitySupport.clean($0, maximumBytes: maximumOutputBytes) }
        result.sourceBlockId = node.sourceBlockId.flatMap { CoreValidation.identifier($0) ? $0 : nil }
        // Only a sub-agent block has a type, a model of its own and an isolation.
        result.identity = node.kind == "agent" ? node.identity?.normalized : nil
        result.figures = node.kind == "agent" ? node.figures?.normalized : nil
        // Only a run's main block keeps its consultations of the advisor.
        result.advisor = node.kind == "main" ? node.advisor?.normalized(restoring: restoring) : nil
        if restoring && !terminal(result.state) { result.state = "stopped" }
        if node.kind == "main" { result.entries = []; return result }

        var remaining = maximumNodeBytes - result.title.utf8.count - (result.input?.utf8.count ?? 0) - (result.output?.utf8.count ?? 0)
            - (result.outputFile?.utf8.count ?? 0) - (result.noticeReply?.utf8.count ?? 0) - (result.sourceBlockId?.utf8.count ?? 0)
            - (result.identity?.byteCount ?? 0)
        var seen = Set<String>()
        var entries: [LogEntry] = []
        for var entry in node.entries.suffix(maximumEntries).reversed() {
            guard CoreValidation.identifier(entry.id), seen.insert(entry.id).inserted,
                  LogEntryKinds.stored.contains(entry.kind),
                  entry.provider.map(ProviderOptions.ids.contains) ?? true,
                  entry.timestamp.utf8.count <= 80, AgentRunTiming.parseTimestamp(entry.timestamp) != nil,
                  remaining > 256 else { continue }
            entry.activity = entry.activity.flatMap { ActivitySupport.normalized($0, restoring: restoring) }
            entry.images = entry.kind == "image" ? AgentImageSupport.normalized(entry.images) : nil
            let metadata = entry.id.utf8.count + entry.timestamp.utf8.count + entry.kind.utf8.count + (entry.provider?.utf8.count ?? 0) + AgentImageSupport.approximateBytes(entry.images)
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
