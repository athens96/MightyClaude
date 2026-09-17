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
    /// "claude" observes stream-json plus Mods; "codex" observes exec JSONL.
    let provider: String
    private let emit: (ExecutionGraphNode) -> Void
    private var codexAgents: [String: String] = [:]
    private var codexRootThread: String?
    private var codexSettledCalls = Set<String>()
    private var codexCallOrder: [String] = []
    private var codexCallGenerations: [String: [String: Int]] = [:]
    private var codexObservedCallOrder: [String] = []
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
    private var taskAliases: [String: String] = [:]
    private var messageUsage: [String: GraphTokenUsage] = [:]
    private var pendingSteers: [String] = []
    private var messageOrder: [String] = []
    private var unnamedUsage = 0
    private var finished = false

    init(runID: String, input: String?, provider: String = "claude", emit: @escaping (ExecutionGraphNode) -> Void) {
        self.runID = runID; mainID = ExecutionGraphSupport.mainNodeID(runId: runID); self.provider = provider; self.emit = emit
        let main = ExecutionGraphNode(id: mainID, runId: runID, kind: "main", state: "running", title: ProviderOptions.label(provider), input: input)
        if let normalized = ExecutionGraphSupport.normalized(main) {
            nodes[mainID] = normalized; order.append(mainID); emit(normalized)
        }
    }

    /// A message the user sent while the turn ran. Claude reads it from stdin
    /// between tool calls; the block under main shows it until the next
    /// root-level answer, which is taken as the reply.
    func steer(id: String, text: String) {
        guard !finished, provider == "claude", nodes.count < ExecutionGraphSupport.maximumNodes else { return }
        let nodeID = ExecutionGraphSupport.identifier(runID, "steer:" + id)
        guard nodes[nodeID] == nil else { return }
        let node = ExecutionGraphNode(id: nodeID, runId: runID, parentId: mainID, kind: "steer", state: "running", title: "중간 요청", input: text)
        guard let normalized = ExecutionGraphSupport.normalized(node) else { return }
        nodes[nodeID] = normalized; order.append(nodeID); emit(normalized)
        pendingSteers.append(nodeID)
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

    @discardableResult private func ensureAgent(toolID: String) -> String? { ensureNode(toolID: toolID, kind: "agent", title: "하위 에이전트") }
    @discardableResult private func ensureNode(toolID: String, kind: String, title: String) -> String? {
        let id = ExecutionGraphSupport.agentNodeID(runId: runID, toolUseId: toolID)
        guard nodes[id] == nil else { return id }
        guard nodes.count < ExecutionGraphSupport.maximumNodes else { return nil }
        let value = ExecutionGraphNode(id: id, runId: runID, kind: kind, state: "running", title: title)
        nodes[id] = value; order.append(id); emit(value)
        return id
    }
    private func update(_ id: String, reopening: Bool = false, _ change: (inout ExecutionGraphNode) -> Void) {
        guard !finished, let previous = nodes[id] else { return }
        var value = previous; change(&value)
        let restart = reopening && provider == "codex" && previous.kind == "agent"
            && (previous.activityGeneration ?? 0) < ExecutionGraphSupport.maximumActivityGeneration
        if restart { value.activityGeneration = (previous.activityGeneration ?? 0) + 1 }
        // Late starts and stdout fallbacks must not resurrect a final snapshot.
        if !restart, ["error", "stopped"].contains(previous.state), value.state != previous.state {
            value.state = previous.state; value.output = previous.output ?? value.output
        } else if !restart, ExecutionGraphSupport.terminal(previous.state), !ExecutionGraphSupport.terminal(value.state) { value.state = previous.state }
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
            if provider == "codex" {
                var ancestor: String? = parent; var seen = Set<String>()
                while let candidate = ancestor, seen.insert(candidate).inserted {
                    if candidate == id { pendingParents.removeValue(forKey: id); return }
                    ancestor = nodes[candidate]?.parentId
                }
            }
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
        if provider == "codex" { consumeCodex(value); return }
        let parentTool = Self.parentToolID(value)
        if let parentTool { ensureAgent(toolID: parentTool) }
        // Over-cap child events are omitted, never reclassified as main text.
        let current = parentTool.map { ExecutionGraphSupport.agentNodeID(runId: runID, toolUseId: $0) } ?? mainID
        let owner: Owner = .node(current)
        let type = value["type"] as? String
        if type == "assistant", let message = value["message"] as? [String: Any] {
            if let usage = GraphTokenUsage.parse(message["usage"]) {
                unnamedUsage += 1
                recordUsage(usage, message: Self.key(message["id"]) ?? Self.key(value["uuid"]) ?? "unnamed:\(unnamedUsage)", node: current)
            }
        }
        if type == "assistant", let message = value["message"] as? [String: Any], let blocks = message["content"] as? [[String: Any]] {
            // A preamble beside a tool_use is not the reply; wait for a text-only answer.
            if parentTool == nil, !pendingSteers.isEmpty, !blocks.contains(where: { $0["type"] as? String == "tool_use" }), let text = Self.text(blocks), !text.isEmpty {
                let answered = pendingSteers; pendingSteers.removeAll()
                for id in answered { update(id) { $0.state = "completed"; $0.output = text } }
            }
            if parentTool != nil, current != mainID, let text = Self.text(blocks), !text.isEmpty {
                let messageKey = Self.key(value["uuid"]) ?? Self.key(message["id"]) ?? ExecutionGraphSupport.identifier(runID, text)
                let id = ExecutionGraphSupport.identifier(runID, current + ":message:" + messageKey)
                append(LogEntry(id: id, kind: "assistant", text: text, provider: provider), to: current)
            }
            for block in blocks where block["type"] as? String == "tool_use" {
                guard let toolID = Self.key(block["id"]) else { continue }
                rememberTool(toolID, owner: owner)
                let input = block["input"] as? [String: Any]
                let background = input?["run_in_background"] as? Bool == true
                if Self.agentTool(block["name"] as? String) {
                    guard let child = ensureAgent(toolID: toolID) else { continue }
                    setParent(child, owner: owner)
                    if background { backgroundTools.insert(toolID) }
                    update(child) { node in
                        if let prompt = input?["prompt"] as? String { node.input = prompt }
                        if let title = (input?["name"] ?? input?["description"] ?? input?["subagent_type"]) as? String, !title.isEmpty { node.title = title }
                    }
                } else if background, let task = ensureNode(toolID: toolID, kind: "task", title: "백그라운드 작업") {
                    // A backgrounded command outlives its tool result. It gets its
                    // own child block; the engine's task notification settles it.
                    setParent(task, owner: owner)
                    backgroundTools.insert(toolID)
                    update(task) { node in
                        let command = input?["command"] as? String
                        if let command { node.input = command }
                        if let title = input?["description"] as? String, !title.isEmpty { node.title = title }
                        else if let command, !command.isEmpty { node.title = command }
                    }
                }
            }
        } else if type == "user", let message = value["message"] as? [String: Any] {
            for block in message["content"] as? [[String: Any]] ?? [] where block["type"] as? String == "tool_result" {
                guard let toolID = Self.key(block["tool_use_id"]) else { continue }
                rememberTool(toolID, owner: owner)
                let child = ExecutionGraphSupport.agentNodeID(runId: runID, toolUseId: toolID)
                guard let node = nodes[child] else { continue }
                if node.kind == "task" { acknowledgeTask(child, block: block); continue }
                // Background Agent tool results acknowledge launch; they are
                // not the agent's answer. Its turn.complete is authoritative.
                guard !backgroundTools.contains(toolID) else { continue }
                update(child) { node in
                    guard !ExecutionGraphSupport.terminal(node.state) else { return }
                    node.state = block["is_error"] as? Bool == true ? "error" : "completed"
                    node.output = Self.text(block["content"])
                }
            }
            // Task completion arrives as an injected user message, not a tool event.
            if let text = Self.text(message["content"]), text.contains("<task-notification>") { settleTasks(in: text) }
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

    // MARK: Codex exec JSONL
    // Codex has no hook bridge. The root thread's items carry the final
    // message and turn usage; `collab_tool_call` items describe subagents by
    // thread ID with a prompt and a state, but not their inner activity.
    private static func codexAgentState(_ status: String) -> String? {
        switch status {
        case "pending_init", "running": return "running"
        case "completed": return "completed"
        case "interrupted", "shutdown": return "stopped"
        case "errored", "not_found": return "error"
        default: return nil
        }
    }
    static func codexUsage(_ value: Any?) -> GraphTokenUsage? {
        guard let object = value as? [String: Any] else { return nil }
        var mapped: [String: Any] = [:]
        for (source, target) in [("input_tokens", "input_tokens"), ("output_tokens", "output_tokens"), ("cached_input_tokens", "cache_read_input_tokens"), ("cache_write_input_tokens", "cache_creation_input_tokens")] {
            if let number = object[source] { mapped[target] = number }
        }
        return GraphTokenUsage.parse(mapped)
    }
    @discardableResult private func ensureCodexAgent(thread: String) -> String? {
        if let id = codexAgents[thread] { return id }
        guard codexAgents.count < ExecutionGraphSupport.maximumNodes, nodes.count < ExecutionGraphSupport.maximumNodes else { return nil }
        let id = ExecutionGraphSupport.identifier(runID, "codex-agent:" + thread)
        let value = ExecutionGraphNode(id: id, runId: runID, parentId: mainID, kind: "agent", state: "running", title: "Codex · " + String(thread.prefix(12)))
        nodes[id] = value; order.append(id); codexAgents[thread] = id; emit(value)
        alias(thread, to: id)
        return id
    }
    private func consumeCodex(_ value: [String: Any]) {
        guard let type = value["type"] as? String else { return }
        if type == "thread.started", let thread = CodexCollaborationItem.key(value["thread_id"]) {
            codexRootThread = thread; alias(thread, to: mainID); return
        }
        if type == "turn.completed" {
            guard let usage = Self.codexUsage(value["usage"]) else { return }
            // Each turn reports its own usage once; sum turns for the main block.
            unnamedUsage += 1
            recordUsage(usage, message: "turn:\(unnamedUsage)", node: mainID)
            return
        }
        guard ["item.started", "item.updated", "item.completed"].contains(type),
              let item = value["item"] as? [String: Any], let itemType = item["type"] as? String else { return }
        if itemType == "agent_message" {
            if type == "item.completed", let text = item["text"] as? String, !text.isEmpty { update(mainID) { $0.output = text } }
            return
        }
        guard let call = CodexCollaborationItem(item) else { return }
        // Completed envelopes may be repeated or followed by a late start.
        // An acknowledged input creates at most one new assignment generation.
        guard !codexSettledCalls.contains(call.id) else { return }
        if codexCallGenerations[call.id] == nil {
            codexObservedCallOrder.append(call.id)
            if codexObservedCallOrder.count > 512 { codexCallGenerations.removeValue(forKey: codexObservedCallOrder.removeFirst()) }
            codexCallGenerations[call.id] = Dictionary(uniqueKeysWithValues: call.threads.map { thread in
                (thread, codexAgents[thread].flatMap { nodes[$0]?.activityGeneration } ?? 0)
            })
        }
        let completed = type == "item.completed"
        let succeeded = completed && call.status == "completed"
        if completed {
            codexSettledCalls.insert(call.id); codexCallOrder.append(call.id)
            if codexCallOrder.count > 512 { codexSettledCalls.remove(codexCallOrder.removeFirst()) }
        }
        let owner: Owner = call.sender.map { sender in
            codexRootThread == nil || sender == codexRootThread ? .node(mainID) : .agent(sender)
        } ?? .node(mainID)
        rememberTool(call.id, owner: owner)
        for thread in call.threads where thread != codexRootThread {
            guard let id = ensureCodexAgent(thread: thread) else { continue }
            // A wait already in flight before a new instruction may finish
            // late with the previous assignment's answer. Keep its tool row,
            // but do not settle or overwrite the newer child generation.
            let addressed = call.receivers.contains(thread)
            let stale = codexCallGenerations[call.id]?[thread].map { $0 < (nodes[id]?.activityGeneration ?? 0) } ?? false
            // Every accepted input is a real assignment, even if two sends
            // were in flight together. Preserve both prompts; stale returned
            // agent states must still not overwrite the newer assignment.
            if stale && !(succeeded && call.tool == "send_input" && addressed) { continue }
            if call.tool == "spawn_agent", addressed {
                // Resolve the sender later if a nested spawn precedes its parent.
                // Never manufacture another root or point a node at itself.
                setParent(id, owner: owner)
            }
            let restarting = succeeded && call.tool == "send_input" && addressed
            if restarting, let output = nodes[id]?.output, !output.isEmpty {
                let generation = nodes[id]?.activityGeneration ?? 0
                append(LogEntry(id: ExecutionGraphSupport.identifier(runID, id + ":answer:\(generation)"), kind: "assistant", text: output, provider: provider), to: id)
            }
            update(id, reopening: restarting) { node in
                if call.tool == "spawn_agent", addressed, let prompt = call.prompt, node.input == nil {
                    node.input = prompt
                    node.title = ActivitySupport.clean(prompt, maximumBytes: 100, singleLine: true) + " · " + String(thread.prefix(12))
                }
                if restarting { node.state = "running"; node.output = nil }
                if !stale, let state = call.states[thread] {
                    if let mapped = Self.codexAgentState(state.status),
                       !(state.status == "shutdown" && node.state == "completed") { node.state = mapped }
                    if let message = state.message, !message.isEmpty { node.output = message }
                }
                // The call's failure reports an operation failure, not a child
                // failure. A successful spawn also does not complete its child.
                if addressed, succeeded, call.tool == "close_agent", !ExecutionGraphSupport.terminal(node.state) {
                    node.state = "stopped"
                }
            }
            if restarting, let prompt = call.prompt {
                append(LogEntry(id: ExecutionGraphSupport.identifier(runID, id + ":input:" + call.id), kind: "user", text: prompt, provider: provider), to: id)
            }
        }
    }

    /// One message is streamed as several events that all carry its usage, so
    /// a block adds each message once and keeps that message's latest figure.
    private func recordUsage(_ usage: GraphTokenUsage, message: String, node: String) {
        guard nodes[node] != nil else { return }
        let previous = messageUsage[message]
        guard previous != usage else { return }
        if previous == nil {
            messageOrder.append(message)
            if messageOrder.count > 1_024 { messageUsage.removeValue(forKey: messageOrder.removeFirst()) }
        }
        messageUsage[message] = usage
        update(node) { $0.usage = ($0.usage ?? GraphTokenUsage()) - (previous ?? GraphTokenUsage()) + usage }
    }

    /// The launch acknowledgement names the engine's task ID. Keep it so a
    /// notification without a tool-use ID can still settle the block.
    private func acknowledgeTask(_ id: String, block: [String: Any]) {
        let text = Self.text(block["content"]) ?? ""
        if block["is_error"] as? Bool == true {
            update(id) { node in node.state = "error"; node.output = text.isEmpty ? nil : text }
            return
        }
        if let match = text.firstMatch(of: #/background with ID: (?<task>[A-Za-z0-9_-]+)/#), taskAliases.count < 512 {
            taskAliases[String(match.task)] = id
        }
        if !text.isEmpty { append(LogEntry(id: ExecutionGraphSupport.identifier(runID, id + ":launch"), kind: "system", text: text, provider: provider), to: id) }
    }
    private static func tag(_ body: Substring, _ name: String) -> String? {
        guard let start = body.range(of: "<\(name)>"), let end = body.range(of: "</\(name)>", range: start.upperBound..<body.endIndex) else { return nil }
        let value = body[start.upperBound..<end.lowerBound].trimmingCharacters(in: .whitespacesAndNewlines)
        return value.isEmpty ? nil : value
    }
    private func settleTasks(in text: String) {
        var remaining = text[...]
        while let start = remaining.range(of: "<task-notification>"), let end = remaining.range(of: "</task-notification>", range: start.upperBound..<remaining.endIndex) {
            let body = remaining[start.upperBound..<end.lowerBound]
            remaining = remaining[end.upperBound...]
            let byTool = Self.tag(body, "tool-use-id").map { ExecutionGraphSupport.agentNodeID(runId: runID, toolUseId: $0) }.flatMap { nodes[$0] != nil ? $0 : nil }
            guard let id = byTool ?? Self.tag(body, "task-id").flatMap({ taskAliases[$0] }), nodes[id]?.kind == "task" else { continue }
            let status = Self.tag(body, "status")?.lowercased() ?? "completed"
            let state = status == "completed" ? "completed" : ["failed", "error"].contains(status) ? "error" : "stopped"
            update(id) { node in
                guard !ExecutionGraphSupport.terminal(node.state) else { return }
                node.state = state
                if let summary = Self.tag(body, "summary") { node.output = summary }
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
            entries: pending.activities.map { LogEntry(id: $0.id, kind: "system", text: $0.summary, provider: provider, activity: $0) })
        guard let bounded = ExecutionGraphSupport.normalized(temporary) else { return PendingAgent() }
        var result = PendingAgent()
        result.activities = bounded.entries.compactMap(\.activity)
        if let metadata {
            result.metadata = ModGraphMetadata(phase: metadata.phase, agentId: Self.key(metadata.agentId), parentAgentId: Self.key(metadata.parentAgentId), parentToolUseId: Self.key(metadata.parentToolUseId), name: bounded.title, input: bounded.input, output: bounded.output)
        }
        return result
    }
    private func appendActivity(_ value: AgentActivity, to id: String) {
        append(LogEntry(id: value.id, kind: "system", text: value.summary, provider: provider, activity: value), to: id)
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
                let text = value.kind == "task" ? "백그라운드 작업의 완료 알림을 받기 전에 실행이 종료되었습니다."
                    : value.kind == "steer" ? "중간 요청에 대한 응답을 받기 전에 실행이 종료되었습니다." : "하위 에이전트의 완료 응답을 받기 전에 실행이 종료되었습니다."
                value.entries.append(LogEntry(id: ExecutionGraphSupport.identifier(runID, id + ":unfinished"), kind: "system", text: text, provider: provider))
            }
        }
        update(mainID) { $0.state = state }
        finished = true
    }
}
