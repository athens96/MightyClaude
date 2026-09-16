import Foundation

/// Owned by CLIStreamParser on the runner actor. This observes public CLI/Mods
/// events; it never reads another session's transcript or guesses child prompts.
final class ExecutionGraphTracker {
    private enum Owner: Equatable { case node(String), agent(String) }
    private struct PendingAgent {
        var metadata: ModGraphMetadata?
        var activities: [AgentActivity] = []
    }
    private let runID: String
    private let mainID: String
    private let emit: (ExecutionGraphNode) -> Void
    private var nodes: [String: ExecutionGraphNode] = [:]
    private var order: [String] = []
    private var aliases: [String: String] = [:]
    private var pendingParents: [String: String] = [:]
    private var pendingAgents: [String: PendingAgent] = [:]
    private var toolOwners: [String: Owner] = [:]
    private var toolOrder: [String] = []
    private var activityOwners: [String: Owner] = [:]
    private var activityOrder: [String] = []
    private var backgroundTools = Set<String>()
    private var finished = false

    init(runID: String, input: String?, emit: @escaping (ExecutionGraphNode) -> Void) {
        self.runID = runID; mainID = ExecutionGraphSupport.mainNodeID(runId: runID); self.emit = emit
        let main = ExecutionGraphNode(id: mainID, runId: runID, kind: "main", state: "running", title: "Claude", input: input)
        if let normalized = ExecutionGraphSupport.normalized(main) {
            nodes[mainID] = normalized; order.append(mainID); emit(normalized)
        }
    }

    static func parentToolID(_ value: [String: Any]) -> String? { key(value["parent_tool_use_id"]) }
    private static func key(_ value: Any?) -> String? {
        guard let value = value as? String, !value.isEmpty, value.utf8.count <= 512 else { return nil }
        return value
    }
    private static func text(_ value: Any?) -> String? {
        if let value = value as? String { return value }
        guard let blocks = value as? [[String: Any]] else { return nil }
        let values = blocks.filter { $0["type"] as? String == "text" }.compactMap { $0["text"] as? String }
        return values.isEmpty ? nil : values.joined(separator: "\n")
    }
    private static func agentTool(_ name: String?) -> Bool { ["agent", "task"].contains(name?.lowercased() ?? "") }

    @discardableResult private func ensureAgent(toolID: String) -> String? {
        let id = ExecutionGraphSupport.agentNodeID(runId: runID, toolUseId: toolID)
        guard nodes[id] == nil else { return id }
        guard nodes.count < ExecutionGraphSupport.maximumNodes else { return nil }
        let value = ExecutionGraphNode(id: id, runId: runID, kind: "agent", state: "running", title: "하위 에이전트")
        nodes[id] = value; order.append(id); emit(value)
        return id
    }
    private func update(_ id: String, _ change: (inout ExecutionGraphNode) -> Void) {
        guard !finished, let previous = nodes[id] else { return }
        var value = previous; change(&value)
        // Late starts and stdout fallbacks must not resurrect a final snapshot.
        if ["error", "stopped"].contains(previous.state), value.state != previous.state {
            value.state = previous.state; value.output = previous.output ?? value.output
        } else if ExecutionGraphSupport.terminal(previous.state), !ExecutionGraphSupport.terminal(value.state) { value.state = previous.state }
        guard var normalized = ExecutionGraphSupport.normalized(value), normalized != previous else { return }
        normalized.updatedAt = mightyTimestamp(); nodes[id] = normalized; emit(normalized)
    }
    private func append(_ entry: LogEntry, to id: String) {
        update(id) { value in
            if let index = value.entries.firstIndex(where: { $0.id == entry.id }) {
                var replacement = entry; replacement.timestamp = value.entries[index].timestamp
                value.entries[index] = replacement
            } else { value.entries.append(entry) }
        }
    }
    private func rememberTool(_ tool: String, owner: Owner) {
        if toolOwners[tool] == nil {
            toolOrder.append(tool)
            if toolOrder.count > 512 { toolOwners.removeValue(forKey: toolOrder.removeFirst()) }
        }
        // A child owner is more specific than a delayed unscoped Mod event.
        if let old = toolOwners[tool], old != .node(mainID), owner == .node(mainID) { return }
        toolOwners[tool] = owner
    }
    private func nodeID(_ owner: Owner) -> String? {
        switch owner { case .node(let id): return id; case .agent(let id): return aliases[id] }
    }
    private func setParent(_ id: String, owner: Owner) {
        if let parent = nodeID(owner), parent != id {
            pendingParents.removeValue(forKey: id)
            update(id) { $0.parentId = parent }
        } else if case .agent(let agent) = owner { pendingParents[id] = agent }
    }
    private func alias(_ agent: String, to id: String) {
        guard aliases[agent] == nil || aliases[agent] == id else { return }
        guard aliases[agent] != nil || aliases.count < 512 else { return }
        aliases[agent] = id
        let waiting = pendingParents.filter { $0.value == agent }.map(\.key)
        for child in waiting { setParent(child, owner: .node(id)) }
        if let pending = pendingAgents.removeValue(forKey: agent) {
            for activity in pending.activities { appendActivity(activity, to: id) }
            if let metadata = pending.metadata { apply(metadata, to: id) }
        }
    }

    /// Observe hierarchy before CLIStreamParser creates the corresponding tool
    /// activity, so child tool rows take the same route as child Markdown.
    func consume(_ value: [String: Any]) {
        guard !finished else { return }
        let parentTool = Self.parentToolID(value)
        if let parentTool { ensureAgent(toolID: parentTool) }
        // Over-cap child events are omitted, never reclassified as main text.
        let current = parentTool.map { ExecutionGraphSupport.agentNodeID(runId: runID, toolUseId: $0) } ?? mainID
        let owner: Owner = .node(current)
        let type = value["type"] as? String
        if type == "assistant", let message = value["message"] as? [String: Any], let blocks = message["content"] as? [[String: Any]] {
            if parentTool != nil, current != mainID, let text = Self.text(blocks), !text.isEmpty {
                let messageKey = Self.key(value["uuid"]) ?? Self.key(message["id"]) ?? ExecutionGraphSupport.identifier(runID, text)
                let id = ExecutionGraphSupport.identifier(runID, current + ":message:" + messageKey)
                append(LogEntry(id: id, kind: "assistant", text: text, provider: "claude"), to: current)
            }
            for block in blocks where block["type"] as? String == "tool_use" {
                guard let toolID = Self.key(block["id"]) else { continue }
                rememberTool(toolID, owner: owner)
                guard Self.agentTool(block["name"] as? String), let child = ensureAgent(toolID: toolID) else { continue }
                setParent(child, owner: owner)
                let input = block["input"] as? [String: Any]
                if input?["run_in_background"] as? Bool == true { backgroundTools.insert(toolID) }
                update(child) { node in
                    if let prompt = input?["prompt"] as? String { node.input = prompt }
                    if let title = (input?["name"] ?? input?["description"] ?? input?["subagent_type"]) as? String, !title.isEmpty { node.title = title }
                }
            }
        } else if type == "user", let message = value["message"] as? [String: Any], let blocks = message["content"] as? [[String: Any]] {
            for block in blocks where block["type"] as? String == "tool_result" {
                guard let toolID = Self.key(block["tool_use_id"]) else { continue }
                rememberTool(toolID, owner: owner)
                let child = ExecutionGraphSupport.agentNodeID(runId: runID, toolUseId: toolID)
                // Background Agent tool results acknowledge launch; they are
                // not the agent's answer. Its turn.complete is authoritative.
                guard nodes[child] != nil, !backgroundTools.contains(toolID) else { continue }
                update(child) { node in
                    guard !ExecutionGraphSupport.terminal(node.state) else { return }
                    node.state = block["is_error"] as? Bool == true ? "error" : "completed"
                    node.output = Self.text(block["content"])
                }
            }
        } else if type == "result" {
            if parentTool == nil {
                // Main output comes only from the CLI's actual root result.
                update(mainID) { $0.output = value["result"] as? String }
            } else if current != mainID {
                update(current) { node in
                    node.output = value["result"] as? String
                    node.state = value["is_error"] as? Bool == true || (value["subtype"] as? String ?? "").hasPrefix("error") ? "error" : "completed"
                }
            }
        }
    }

    func receiveMod(_ value: ModMetadata) {
        guard !finished else { return }
        if let tool = Self.key(value.toolUseId) {
            if let agent = Self.key(value.agentId) { rememberTool(tool, owner: .agent(agent)) }
            else if value.event == "tool.call" || value.event == "tool.complete" { rememberTool(tool, owner: .node(mainID)) }
        }
        guard let metadata = value.graph, metadata.version == 1 else { return }
        let id: String?
        if let toolID = Self.key(metadata.parentToolUseId) {
            id = ensureAgent(toolID: toolID)
            if let id {
                let parent: Owner = Self.key(metadata.parentAgentId).map(Owner.agent) ?? .node(mainID)
                setParent(id, owner: parent)
                // The Agent call belongs to its parent, never to the spawned child.
                rememberTool(toolID, owner: parent)
                if let agent = Self.key(metadata.agentId) { alias(agent, to: id) }
            }
        } else { id = Self.key(metadata.agentId).flatMap { aliases[$0] } }
        if let id { apply(metadata, to: id) }
        else if let agent = Self.key(metadata.agentId), pendingAgents[agent] != nil || pendingAgents.count < ExecutionGraphSupport.maximumNodes {
            var pending = pendingAgents[agent] ?? PendingAgent()
            // Keep a final observation when a late starting event arrives.
            let previousState = pending.metadata?.phase
            if previousState == "error" || previousState == "stopped" {
                if metadata.phase == previousState { pending.metadata = metadata }
            } else if pending.metadata.map({ ExecutionGraphSupport.terminal($0.phase) }) != true || ExecutionGraphSupport.terminal(metadata.phase) {
                pending.metadata = metadata
            }
            pendingAgents[agent] = boundedPending(pending, agent: agent)
        }
    }
    private func apply(_ metadata: ModGraphMetadata, to id: String) {
        update(id) { node in
            if let title = metadata.name ?? metadata.agentType, !title.isEmpty { node.title = title }
            if let input = metadata.input { node.input = input }
            if let output = metadata.output { node.output = output }
            let state = metadata.phase == "starting" ? "running" : metadata.phase
            if ActivitySupport.states.contains(state) { node.state = state }
        }
    }

    /// Return true for a known child owner, including an agent whose spawn
    /// envelope is still in flight. Such rows must never leak into main logs.
    func activity(_ value: AgentActivity, toolID: String? = nil) -> Bool {
        guard !finished else { return false }
        let owner = toolID.flatMap { toolOwners[$0] } ?? activityOwners[value.id] ?? .node(mainID)
        if activityOwners[value.id] == nil {
            activityOrder.append(value.id)
            if activityOrder.count > 512 { activityOwners.removeValue(forKey: activityOrder.removeFirst()) }
        }
        activityOwners[value.id] = owner
        if let id = nodeID(owner) {
            guard id != mainID else { return false }
            appendActivity(value, to: id)
        } else if case .agent(let agent) = owner, pendingAgents[agent] != nil || pendingAgents.count < ExecutionGraphSupport.maximumNodes {
            var pending = pendingAgents[agent] ?? PendingAgent()
            if let index = pending.activities.firstIndex(where: { $0.id == value.id }) { pending.activities[index] = value }
            else { pending.activities.append(value); pending.activities = Array(pending.activities.suffix(ExecutionGraphSupport.maximumEntries)) }
            pendingAgents[agent] = boundedPending(pending, agent: agent)
        }
        return true
    }
    private func boundedPending(_ pending: PendingAgent, agent: String) -> PendingAgent {
        let metadata = pending.metadata
        let temporary = ExecutionGraphNode(id: ExecutionGraphSupport.identifier(runID, "pending:" + agent), runId: runID,
            kind: "agent", state: "running", title: metadata?.name ?? metadata?.agentType ?? "하위 에이전트",
            input: metadata?.input, output: metadata?.output,
            entries: pending.activities.map { LogEntry(id: $0.id, kind: "system", text: $0.summary, provider: "claude", activity: $0) })
        guard let bounded = ExecutionGraphSupport.normalized(temporary) else { return PendingAgent() }
        var result = PendingAgent()
        result.activities = bounded.entries.compactMap(\.activity)
        if let metadata {
            result.metadata = ModGraphMetadata(phase: metadata.phase, agentId: Self.key(metadata.agentId), parentAgentId: Self.key(metadata.parentAgentId), parentToolUseId: Self.key(metadata.parentToolUseId), name: bounded.title, input: bounded.input, output: bounded.output)
        }
        return result
    }
    private func appendActivity(_ value: AgentActivity, to id: String) {
        append(LogEntry(id: value.id, kind: "system", text: value.summary, provider: "claude", activity: value), to: id)
    }

    func finish(state: String) {
        guard !finished, ExecutionGraphSupport.terminal(state) else { return }
        // A dropped spawn may leave only an actual agent ID. Preserve those
        // observed results without inventing a parent or prompt.
        for (agent, pending) in pendingAgents.sorted(by: { $0.key < $1.key }) {
            guard nodes.count < ExecutionGraphSupport.maximumNodes else { break }
            let id = ExecutionGraphSupport.identifier(runID, "agent:" + agent)
            let node = ExecutionGraphNode(id: id, runId: runID, kind: "agent", state: "running", title: "하위 에이전트")
            nodes[id] = node; order.append(id); emit(node)
            aliases[agent] = id
            for activity in pending.activities { appendActivity(activity, to: id) }
            if let metadata = pending.metadata { apply(metadata, to: id) }
        }
        pendingAgents.removeAll()
        for (id, agent) in pendingParents { if let parent = aliases[agent], id != parent { update(id) { $0.parentId = parent } } }
        for id in order where id != mainID {
            guard let node = nodes[id], !ExecutionGraphSupport.terminal(node.state) else { continue }
            // Process success does not prove that an unfinished background
            // agent succeeded or returned an answer.
            update(id) { value in
                value.state = state == "error" ? "error" : "stopped"
                value.entries.append(LogEntry(id: ExecutionGraphSupport.identifier(runID, id + ":unfinished"), kind: "system", text: "하위 에이전트의 완료 응답을 받기 전에 실행이 종료되었습니다.", provider: "claude"))
            }
        }
        update(mainID) { $0.state = state }
        finished = true
    }
}
