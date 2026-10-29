import Foundation

/// Owned by CLIStreamParser on the runner actor. This observes public CLI/Mods
/// events and never guesses child prompts. The one other source is Codex's own
/// session records for this run's subagents (`CodexSessionWatcher`): only
/// children spawned from this run's root thread, only work done since the run
/// started, and only readable parts. Sealed message bodies, role instructions
/// and history a child copied from its parent are never shown; command output
/// is, as for the root's exec items. Antigravity's sub-agents are read the same
/// way from their own transcripts (`AntigravitySubagentWatcher`).
final class ExecutionGraphTracker {
    private enum Owner: Equatable { case node(String), agent(String) }
    private struct PendingAgent {
        var metadata: ModGraphMetadata?
        var activities: [AgentActivity] = []
    }
    private let runID: String
    private let mainID: String
    /// "claude" observes stream-json plus Mods; "codex" observes exec JSONL;
    /// "gemini" observes Antigravity's (`agy`) stream-json.
    let provider: String
    /// Model the run was configured with; used for Codex response records.
    private let configuredModel: String?
    private let emit: (ExecutionGraphNode) -> Void
    private var codexAgents: [String: String] = [:]
    private(set) var codexRootThread: String?
    private var codexSessionTurns: [String: Int] = [:]
    private var codexSettledCalls = Set<String>()
    private var codexCallOrder: [String] = []
    private var codexCallGenerations: [String: [String: Int]] = [:]
    private var codexObservedCallOrder: [String] = []
    /// The Antigravity conversation this run continues or started.
    private(set) var antigravityConversation: String?
    /// Sub-agents the stream named, oldest first, for the runner's watcher.
    private(set) var antigravitySubagents: [AntigravitySubagent] = []
    private var antigravityAgents: [String: String] = [:]
    private var antigravityTurns: [String: Int] = [:]
    private var antigravityModel: String?
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
    /// Engine task IDs and background agent IDs → block: a notification or hand-back without a tool-use ID finds its block by them.
    private var taskAliases: [String: String] = [:]
    /// Agent calls' `name` inputs → block: a hand-back may name its agent by that instead of its ID.
    private var agentNames: [String: String] = [:]
    /// The notification summary an agent block's result holds until its hand-back's report replaces it.
    private var agentSummaries: [String: String] = [:]
    private var messageUsage: [String: GraphTokenUsage] = [:]
    private var messageModel: [String: String] = [:]
    private var messageActivityIds: [String: [String]] = [:]
    private var messageConfigured: [String: Bool] = [:]
    private var nodeResponseOrder: [String: [String]] = [:]
    private var pendingSteers: [String] = []
    /// The request's own root result arrived; a user's steer after it opens a turn whose answer follows in the result.
    private var turnEnded = false
    /// What the steer that opened such a turn is called (중간 요청): its answer's section is headed by it. A delegation
    /// notice after the result opens a notice run instead (`joinTurn`).
    private var steeredTurn: String?
    /// Notice runs opened so far (`BackgroundNoticeRuns`); the next one is numbered one more.
    private var noticeIndex = 0
    /// The notice run of the turn a root-level notice opened after the request's own turn: that turn's result is its result.
    private var noticeTurn: String?
    /// The notice run, complete at once and without a result, that notices taken into a running turn share until its result.
    private var absorbedRun: String?
    /// The earlier notice run a notice turn answers in again while it concerns only blocks copied before. A turn
    /// about blocks of two earlier runs answers in the first one it names.
    private var mergeTurn: String?
    /// The state `mergeTurn`'s main had before the turn reopened it, given back if the turn brings new work after all.
    private var mergeRestore: String?
    /// Each notice run's copies of finished blocks, oldest first.
    private var noticeCopies: [String: [String]] = [:]
    /// Each copied block's latest notice run.
    private var copiedIn: [String: String] = [:]
    /// What opened each notice run and what it names while it holds no copy (`BackgroundNoticeRuns.title(kind:names:)`).
    /// `senders` says who each name stands for (its agent block, or the name when no block sent it), so a sender is named once.
    private typealias NoticeOpening = (opener: BackgroundNoticeRuns.Opener, names: [String], lines: [String], senders: [String])
    private var noticeOpeners: [String: NoticeOpening] = [:]
    /// A root message's line no run took yet, with the name it gives a run (a known sender's block title) and the agent block
    /// that sent it (nil: finished work, a delegation notice, a sender this run does not know).
    private struct HeldLine { var line: String?; var name: String?; var agent: String? }
    /// Lines of root messages this turn no run took yet (in or before the request's own turn), with their names: a notice answer
    /// opens a run naming them.
    private var turnLines: [HeldLine] = []
    /// The most lines and names a notice run's opener, or a turn waiting for its answer, holds.
    private static let noticeLineLimit = 16
    /// The most 하위 에이전트 메시지 runs one request opens; past it a message turn answers in the latest one again.
    private static let messageRunLimit = 8
    /// Nodes a 하위 에이전트 메시지 run may not take, so the copies finished work brings, and the 백그라운드 작업 완료 run
    /// that holds them, still fit under `ExecutionGraphSupport.maximumNodes`.
    private static let noticeHeadroom = 16
    /// 하위 에이전트 메시지 runs opened so far, and the latest one.
    private var messageRunCount = 0
    private var latestMessageRun: String?
    /// The agent block whose messages alone a 하위 에이전트 메시지 run holds: that agent's next message turn answers in it again
    /// while no other run came after it.
    private var messageAgents: [String: String] = [:]
    private var messageOrder: [String] = []
    private var unnamedUsage = 0
    private var compactions = 0
    private var finished = false

    init(runID: String, input: String?, provider: String = "claude", configuredModel: String? = nil, emit: @escaping (ExecutionGraphNode) -> Void) {
        self.runID = runID; mainID = ExecutionGraphSupport.mainNodeID(runId: runID); self.provider = provider; self.configuredModel = configuredModel; self.emit = emit
        let main = ExecutionGraphNode(id: mainID, runId: runID, kind: "main", state: "running", title: ProviderOptions.label(provider), input: input)
        if let normalized = ExecutionGraphSupport.normalized(main) {
            nodes[mainID] = normalized; order.append(mainID); emit(normalized)
        }
    }

    /// A message the user sent while the turn ran. Claude reads it from stdin
    /// between tool calls; the block under main shows it until the next
    /// root-level answer, which is taken as the reply.
    func steer(id: String, text: String) {
        // After the request's result the steer starts a new turn in the same process.
        guard addSteer(id: id, text: text), turnEnded, steeredTurn == nil else { return }
        // A delegation notice's answer is its own 위임 알림 run's; the block stays under the request and takes the reply.
        if MightyGraphSupport.isDelegationNotice(text) {
            joinTurn(.delegation, name: nil, line: text)
            // No room for its run: its answer follows in the request's result under 위임 알림, as before notice runs, rather than replace it.
            if noticeTurn == nil, mergeTurn == nil { steeredTurn = L("graph.block.delegationNotice") }
        } else { steeredTurn = L("graph.block.steer") }
    }
    @discardableResult
    private func addSteer(id: String, text: String) -> Bool {
        guard !finished, provider == "claude", nodes.count < ExecutionGraphSupport.maximumNodes else { return false }
        let nodeID = ExecutionGraphSupport.identifier(runID, "steer:" + id)
        guard nodes[nodeID] == nil else { return false }
        let node = ExecutionGraphNode(id: nodeID, runId: runID, parentId: mainID, kind: "steer", state: "running", title: L("graph.block.steer"), input: text)
        guard let normalized = ExecutionGraphSupport.normalized(node) else { return false }
        nodes[nodeID] = normalized; order.append(nodeID); emit(normalized)
        pendingSteers.append(nodeID)
        return true
    }

    /// The CLI summarized its context. The block sits under the request, or
    /// for Claude under the subagent that compacted (Codex exec items carry no
    /// thread), and is complete the moment it arrives.
    func compaction(key: String, owner: String, detail: String) {
        guard !finished, nodes.count < ExecutionGraphSupport.maximumNodes else { return }
        let nodeID = ExecutionGraphSupport.identifier(runID, "compact:" + key)
        guard nodes[nodeID] == nil else { return }
        let parent = nodes[owner] != nil ? owner : mainID
        let node = ExecutionGraphNode(id: nodeID, runId: runID, parentId: parent, kind: "compact", state: "completed", title: ContextCompaction.title, input: detail)
        guard let normalized = ExecutionGraphSupport.normalized(node) else { return }
        nodes[nodeID] = normalized; order.append(nodeID); emit(normalized)
    }

    /// Header and readable body of an AskUserQuestion input: every question with its options.
    static func questionSummary(_ input: [String: Any]?) -> (title: String?, body: String) {
        let questions = input?["questions"] as? [[String: Any]] ?? []
        let title = (questions.first?["header"] as? String).flatMap { $0.isEmpty ? nil : L("graph.block.questionPrefix") + $0 }
        let body = questions.map { question -> String in
            let text = question["question"] as? String ?? ""
            let options = (question["options"] as? [[String: Any]] ?? []).compactMap { $0["label"] as? String }
            return options.isEmpty ? text : text + "\n" + options.map { "  ○ " + $0 }.joined(separator: "\n")
        }.joined(separator: "\n\n")
        return (title, body)
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

    @discardableResult private func ensureAgent(toolID: String) -> String? { ensureNode(toolID: toolID, kind: "agent", title: L("graph.block.agent")) }
    @discardableResult private func ensureNode(toolID: String, kind: String, title: String) -> String? {
        let id = ExecutionGraphSupport.agentNodeID(runId: runID, toolUseId: toolID)
        guard nodes[id] == nil else { return id }
        guard nodes.count < ExecutionGraphSupport.maximumNodes else { return nil }
        let value = ExecutionGraphNode(id: id, runId: runID, kind: kind, state: "running", title: title)
        nodes[id] = value; order.append(id); emit(value)
        return id
    }
    /// `continuing`: an Antigravity sub-agent that answered while helpers it
    /// started were still out goes on with the same assignment when one reports.
    private func update(_ id: String, reopening: Bool = false, continuing: Bool = false, _ change: (inout ExecutionGraphNode) -> Void) {
        guard !finished, let previous = nodes[id] else { return }
        var value = previous; change(&value)
        let restart = reopening && ["codex", "gemini"].contains(provider) && previous.kind == "agent"
            && (previous.activityGeneration ?? 0) < ExecutionGraphSupport.maximumActivityGeneration
        if restart { value.activityGeneration = (previous.activityGeneration ?? 0) + 1 }
        // Late starts and stdout fallbacks must not resurrect a final snapshot.
        if !restart, ["error", "stopped"].contains(previous.state), value.state != previous.state {
            value.state = previous.state; value.output = previous.output ?? value.output
        } else if !restart, ExecutionGraphSupport.terminal(previous.state), !ExecutionGraphSupport.terminal(value.state),
                  !(continuing && provider == "gemini" && previous.kind == "agent" && previous.state == "completed" && value.state == "running") { value.state = previous.state }
        guard var normalized = ExecutionGraphSupport.normalized(value), normalized != previous else { return }
        normalized.updatedAt = mightyTimestamp(); nodes[id] = normalized; emit(normalized)
    }
    private func append(_ entry: LogEntry, to id: String) {
        update(id) { Self.upsert(entry, into: &$0.entries) }
    }
    private static func upsert(_ entry: LogEntry, into entries: inout [LogEntry]) {
        if let index = entries.firstIndex(where: { $0.id == entry.id }) {
            var replacement = entry; replacement.timestamp = entries[index].timestamp
            entries[index] = replacement
        } else { entries.append(entry) }
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
        if provider == "gemini" { consumeAntigravity(value); return }
        let parentTool = Self.parentToolID(value)
        if let parentTool { ensureAgent(toolID: parentTool) }
        // Over-cap child events are omitted, never reclassified as main text.
        let current = parentTool.map { ExecutionGraphSupport.agentNodeID(runId: runID, toolUseId: $0) } ?? mainID
        let owner: Owner = .node(current)
        let type = value["type"] as? String
        if type == "assistant", let message = value["message"] as? [String: Any] {
            let model = message["model"] as? String
            let contentBlocks = message["content"] as? [[String: Any]] ?? []
            let activityIds = contentBlocks.compactMap { block -> String? in
                guard block["type"] as? String == "tool_use" else { return nil }
                return Self.key(block["id"])
            }
            if let usage = GraphTokenUsage.parse(message["usage"]) {
                unnamedUsage += 1
                recordUsage(usage, message: Self.key(message["id"]) ?? Self.key(value["uuid"]) ?? "unnamed:\(unnamedUsage)", node: current, model: model, activityIds: activityIds)
            }
        }
        if type == "system", value["subtype"] as? String == "compact_boundary" {
            compactions += 1
            compaction(key: Self.key(value["uuid"]) ?? "n\(compactions)", owner: current, detail: ContextCompaction.claudeSummary(value["compact_metadata"]))
            return
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
                    if let name = input?["name"] as? String, !name.isEmpty, name.utf8.count <= 512, agentNames[name] != nil || agentNames.count < 512 { agentNames[name] = child }
                    update(child) { node in
                        if background { node.background = true }
                        if let prompt = input?["prompt"] as? String { node.input = prompt }
                        if let title = (input?["name"] ?? input?["description"] ?? input?["subagent_type"]) as? String, !title.isEmpty { node.title = title }
                    }
                } else if block["name"] as? String == "AskUserQuestion", let question = ensureNode(toolID: toolID, kind: "question", title: L("graph.block.question")) {
                    // Each question to the user is its own block; the answer settles it.
                    setParent(question, owner: owner)
                    let summary = Self.questionSummary(input)
                    update(question) { node in
                        node.state = "waiting"
                        if let title = summary.title { node.title = title }
                        node.input = summary.body
                    }
                } else if background, let task = ensureNode(toolID: toolID, kind: "task", title: L("graph.block.task")) {
                    // A backgrounded command outlives its tool result. It gets its
                    // own child block; the engine's task notification settles it.
                    setParent(task, owner: owner)
                    backgroundTools.insert(toolID)
                    update(task) { node in
                        node.background = true
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
                if node.kind == "question" {
                    update(child) { node in
                        node.state = block["is_error"] as? Bool == true ? "stopped" : "completed"
                        node.output = Self.text(block["content"])
                    }
                    continue
                }
                // Background Agent tool results acknowledge launch; they are
                // not the agent's answer. Its turn.complete, notification or hand-back is.
                guard !backgroundTools.contains(toolID) else { rememberAgentID(child, block: block); continue }
                update(child) { node in
                    guard !ExecutionGraphSupport.terminal(node.state) else { return }
                    node.state = block["is_error"] as? Bool == true ? "error" : "completed"
                    node.output = Self.text(block["content"])
                }
            }
            // Task completion arrives as an injected user message, not a tool event; so does a sub-agent's final report.
            // Either settles its block and copies it into a notice run; only a root-level one opens a notice turn.
            if let text = Self.text(message["content"]) {
                if text.contains("<task-notification>") { settleTasks(in: text, notice: parentTool == nil) }
                if text.contains(AgentHandback.opening) { settleHandbacks(in: text, origin: value["origin"] as? [String: Any], notice: parentTool == nil) }
            }
        } else if type == "result", ClaudeStream.isNotificationResult(value) || (parentTool == nil && (noticeTurn != nil || mergeTurn != nil)) {
            guard parentTool == nil else { return }
            let run = noticeTurn, merged = mergeTurn, absorbed = absorbedRun, lines = turnLines
            clearNoticeTurn()
            let answer = (value["result"] as? String)?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
            let failed = value["is_error"] as? Bool == true || (value["subtype"] as? String ?? "").hasPrefix("error")
            if let run {
                // A notice turn's answer is its own run's result; the request's result stays as it was.
                update(ExecutionGraphSupport.mainNodeID(runId: run)) { node in
                    if !answer.isEmpty { node.output = answer }
                    node.state = failed ? "error" : "completed"
                }
                return
            }
            if let merged {
                // A turn about blocks an earlier notice run already holds goes on in that run's result, as a new paragraph.
                if !answer.isEmpty {
                    update(ExecutionGraphSupport.mainNodeID(runId: merged)) { node in
                        node.output = (node.output?.isEmpty ?? true) ? answer : node.output! + "\n\n" + answer
                    }
                }
                setNoticeState(merged, failed ? "error" : "completed")
                return
            }
            // A notice answer no notice run took never extends the request's result: it is the answer of the run its notices
            // were taken into, or of a run of its own.
            guard !answer.isEmpty else { return }
            if let absorbed {
                // The lines this turn held are about the same work: they join the run's request.
                addOpening(absorbed, lines)
                update(ExecutionGraphSupport.mainNodeID(runId: absorbed)) { $0.output = answer }
                if failed { setNoticeState(absorbed, "error") }
                return
            }
            let peer = (value["origin"] as? [String: Any])?["kind"] as? String == "peer"
            let opener: BackgroundNoticeRuns.Opener = peer ? .message : .work
            if opener == .message, let run = messageRun(lines) { answerAgain(in: run, lines, answer: answer, failed: failed); return }
            let named = Self.senders(lines)
            func openOwn(reserving: Bool) -> Bool {
                guard let opened = openNoticeRun(running: false, opener: opener, names: named.names, lines: lines.compactMap(\.line), senders: named.senders,
                                                 agent: Self.soleAgent(lines), reserving: reserving) else { return false }
                update(ExecutionGraphSupport.mainNodeID(runId: opened)) { $0.output = answer }
                if failed { setNoticeState(opened, "error") }
                return true
            }
            if openOwn(reserving: true) { return }
            // No room for a run of its own: the answer goes on in the latest notice run. It is never dropped.
            if let latest = latestNoticeRun { answerAgain(in: latest, lines, answer: answer, failed: failed); return }
            if opener == .message {
                // A message's answer may take the nodes kept for work copies rather than become a section of the request's result;
                // at the very cap it joins the log of the one agent that sent the messages, which adds no node.
                if openOwn(reserving: false) { return }
                if let agent = Self.soleAgent(lines), nodes[agent] != nil {
                    append(LogEntry(id: ExecutionGraphSupport.identifier(runID, agent + ":reply:" + answer), kind: "assistant",
                                    text: "**" + L("graph.background.reply") + "**\n\n" + answer, provider: provider), to: agent)
                    return
                }
            }
            // The last resort, at the very cap with nowhere else (work this run never started, a sender it does not know): the answer
            // follows in the request's result under its kind, as before notice runs.
            update(mainID) { $0.output = MightyGraphSupport.withNoticeSection($0.output, header: L("graph.result.afterSteer", ["block": BackgroundNoticeRuns.kind(opener)]), answer: answer) }
        } else if type == "result" {
            if parentTool == nil, let steered = steeredTurn {
                // A turn a steer opened after the request's result: its answer follows in the result, as a notice turn's does.
                clearNoticeTurn()
                if let answer = (value["result"] as? String)?.trimmingCharacters(in: .whitespacesAndNewlines), !answer.isEmpty {
                    update(mainID) { $0.output = MightyGraphSupport.withNoticeSection($0.output, header: L("graph.result.afterSteer", ["block": steered]), answer: answer) }
                }
            } else if parentTool == nil {
                turnEnded = true
                // Main output comes only from the CLI's actual root result. The request's own turn ended: it reads
                // completed while background tasks it started still run; the process's end settles the rest.
                // The lines its turn held are the request's own answer's, not a notice run's.
                absorbedRun = nil; turnLines = []
                let failed = value["is_error"] as? Bool == true || (value["subtype"] as? String ?? "").hasPrefix("error")
                update(mainID) { node in
                    node.output = value["result"] as? String
                    if !failed { node.state = "completed" }
                }
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
            recordUsage(usage, message: "turn:\(unnamedUsage)", node: mainID, model: configuredModel, markedAsConfigured: configuredModel != nil)
            return
        }
        guard ["item.started", "item.updated", "item.completed"].contains(type),
              let item = value["item"] as? [String: Any], let itemType = item["type"] as? String else { return }
        if itemType == "agent_message" {
            if type == "item.completed", let text = item["text"] as? String, !text.isEmpty { update(mainID) { $0.output = text } }
            return
        }
        if itemType == "context_compaction" {
            guard type == "item.completed" else { return }
            compactions += 1
            compaction(key: Self.key(item["id"]) ?? "n\(compactions)", owner: mainID, detail: ContextCompaction.codexSummary)
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

    // MARK: Codex session records
    // Multi-agent v2 exec output names no spawned thread; the child's own
    // session record does. Both paths share one node per thread ID.
    func codexSession(_ agent: CodexSessionAgent) {
        guard !finished, provider == "codex", agent.thread != codexRootThread, let id = ensureCodexAgent(thread: agent.thread) else { return }
        setParent(id, owner: agent.parentThread == codexRootThread ? .node(mainID) : .agent(agent.parentThread))
        let seenTurns = codexSessionTurns[agent.thread] ?? 0
        codexSessionTurns[agent.thread] = max(seenTurns, agent.turns)
        // A new turn on a settled child is new work, as with send_input.
        let reopening = seenTurns > 0 && agent.turns > seenTurns && nodes[id].map { ExecutionGraphSupport.terminal($0.state) } == true
        var replaced: [(previous: GraphTokenUsage, usage: GraphTokenUsage)] = []
        for record in agent.usage {
            guard let previous = noteUsage(record.usage, message: "codex-session:" + agent.thread + ":" + record.responseId, node: id,
                                           model: record.model, activityIds: [], markedAsConfigured: false) else { continue }
            replaced.append((previous, record.usage))
        }
        let records = replaced.isEmpty ? nil : buildResponseRecords(for: id)
        // The child's records keep each turn's answer; add one only if they did not.
        if reopening, let output = nodes[id]?.output, !output.isEmpty, !agent.entries.contains(where: { $0.kind == "assistant" && $0.text == output }) {
            let generation = nodes[id]?.activityGeneration ?? 0
            append(LogEntry(id: ExecutionGraphSupport.identifier(runID, id + ":answer:\(generation)"), kind: "assistant", text: output, provider: provider), to: id)
        }
        update(id, reopening: reopening) { node in
            if let title = agent.title { node.title = title }
            if node.input == nil, let input = agent.input { node.input = input }
            // Snapshots carry only new or changed entries, so ones the size
            // budget already trimmed are never added back as the newest.
            for entry in agent.entries {
                let known = node.entries.contains { $0.id == entry.id }
                if !known, agent.answers.contains(entry.id), node.entries.contains(where: { $0.kind == "assistant" && $0.text == entry.text }) { continue }
                Self.upsert(entry, into: &node.entries)
            }
            if reopening || agent.output != nil { node.output = agent.output }
            node.state = agent.state
            if let records {
                node.usage = replaced.reduce(node.usage ?? GraphTokenUsage()) { $0 - $1.previous + $1.usage }
                node.responseRecords = records
            }
        }
    }

    // MARK: Antigravity stream-json
    // agy has no hook bridge either. Each `agent_response` step carries its
    // own usage; the reply text reaches the main block through the parser
    // (`antigravityAnswer`), so it is exactly what the transcript shows. A
    // `subagent` step names each sub-agent's conversation, role and prompt;
    // what the sub-agent does is read from its own transcript.
    private func consumeAntigravity(_ value: [String: Any]) {
        guard let event = value["event"] as? String else { return }
        let payload = value[event] as? [String: Any] ?? [:]
        switch event {
        case "init":
            if let conversation = Self.key(value["conversation_id"]) { antigravityConversation = conversation; alias(conversation, to: mainID) }
            if let model = payload["model"] as? String, CoreValidation.model(model) { antigravityModel = model }
        case "step_update":
            let index = payload["step_index"] as? Int ?? -1
            switch payload["step_type"] as? String {
            case "agent_response":
                guard let usage = AntigravityRecords.usage(payload["usage"]) else { return }
                recordUsage(usage, message: "step:\(index)", node: mainID, model: antigravityModel ?? configuredModel, markedAsConfigured: antigravityModel == nil && configuredModel != nil)
            case "subagent":
                let failed = (payload["state"] as? String)?.uppercased() == "ERROR"
                for spec in ((payload["subagent_info"] as? [String: Any])?["subagents"] as? [[String: Any]] ?? []).prefix(ExecutionGraphSupport.maximumNodes) {
                    guard let conversation = AntigravityRecords.conversationID(spec["conversation_id"]), conversation != antigravityConversation,
                          let id = ensureAntigravityAgent(conversation: conversation) else { continue }
                    let title = AntigravityRecords.title(role: spec["role"], typeName: spec["type_name"])
                    let prompt = (spec["initial_prompt"] as? String).flatMap { $0.isEmpty ? nil : $0 }
                    if !antigravitySubagents.contains(where: { $0.conversation == conversation }) {
                        antigravitySubagents.append(AntigravitySubagent(conversation: conversation, parent: antigravityConversation ?? "", title: title, prompt: prompt))
                    }
                    update(id) { node in
                        if let title { node.title = title }
                        if node.input == nil, let prompt { node.input = prompt }
                        // The step ends once the sub-agent is started; only a failed start ends the block.
                        if failed { node.state = "error" }
                    }
                }
            default: break
            }
        default: break
        }
    }
    /// The reply the parser just showed for an `agent_response` step (or the
    /// result's full response when nothing was streamed): the latest one is
    /// the request's answer.
    func antigravityAnswer(_ text: String) {
        guard !finished, provider == "gemini", !text.isEmpty else { return }
        update(mainID) { $0.output = text }
    }
    @discardableResult private func ensureAntigravityAgent(conversation: String) -> String? {
        if let id = antigravityAgents[conversation] { return id }
        guard nodes.count < ExecutionGraphSupport.maximumNodes else { return nil }
        let id = ExecutionGraphSupport.identifier(runID, "agy-agent:" + conversation)
        let value = ExecutionGraphNode(id: id, runId: runID, parentId: mainID, kind: "agent", state: "running", title: L("graph.block.agent"))
        nodes[id] = value; order.append(id); antigravityAgents[conversation] = id; emit(value)
        alias(conversation, to: id)
        return id
    }
    /// A sub-agent's transcript snapshot (`AntigravityChildRecord`). A new
    /// instruction on a settled sub-agent is new work, as Codex's `send_input`.
    func antigravitySession(_ agent: CodexSessionAgent) {
        guard !finished, provider == "gemini", agent.thread != antigravityConversation, let id = ensureAntigravityAgent(conversation: agent.thread) else { return }
        setParent(id, owner: agent.parentThread == antigravityConversation ? .node(mainID) : .agent(agent.parentThread))
        let seenTurns = antigravityTurns[agent.thread] ?? 0
        antigravityTurns[agent.thread] = max(seenTurns, agent.turns)
        let reopening = seenTurns > 0 && agent.turns > seenTurns && nodes[id].map { ExecutionGraphSupport.terminal($0.state) } == true
        var replaced: [(previous: GraphTokenUsage, usage: GraphTokenUsage)] = []
        for record in agent.usage {
            guard let previous = noteUsage(record.usage, message: record.responseId, node: id, model: antigravityModel ?? configuredModel,
                                           activityIds: record.activityIds, markedAsConfigured: antigravityModel == nil && configuredModel != nil) else { continue }
            replaced.append((previous, record.usage))
        }
        let records = replaced.isEmpty ? nil : buildResponseRecords(for: id)
        // The previous assignment's answer stays in the block above the new one.
        if reopening, let node = nodes[id], let output = node.output, !output.isEmpty, !node.entries.contains(where: { $0.kind == "assistant" && $0.text == output }) {
            append(LogEntry(id: ExecutionGraphSupport.identifier(runID, id + ":answer:\(node.activityGeneration ?? 0)"), kind: "assistant", text: output, provider: provider), to: id)
        }
        let continuing = !reopening && agent.state == "running" && nodes[id]?.state == "completed"
        update(id, reopening: reopening, continuing: continuing) { node in
            if let title = agent.title { node.title = title }
            if node.input == nil, let input = agent.input, !input.isEmpty { node.input = input }
            for entry in agent.entries { Self.upsert(entry, into: &node.entries) }
            if reopening || agent.output != nil { node.output = agent.output }
            node.state = agent.state
            if let records {
                node.usage = replaced.reduce(node.usage ?? GraphTokenUsage()) { $0 - $1.previous + $1.usage }
                node.responseRecords = records
            }
        }
    }

    /// One message is streamed as several events that all carry its usage, so
    /// a block adds each message once and keeps that message's latest figure.
    /// On first observation the model and activity IDs are recorded; subsequent
    /// events only update the usage figure (the re-sent response constraint).
    private func recordUsage(_ usage: GraphTokenUsage, message: String, node: String, model: String? = nil, activityIds: [String] = [], markedAsConfigured: Bool = false) {
        guard let previous = noteUsage(usage, message: message, node: node, model: model, activityIds: activityIds, markedAsConfigured: markedAsConfigured) else { return }
        let records = buildResponseRecords(for: node)
        update(node) { $0.usage = ($0.usage ?? GraphTokenUsage()) - previous + usage; $0.responseRecords = records }
    }
    /// Remember one message's latest usage. Returns the figure it replaces,
    /// or nil when nothing changed.
    private func noteUsage(_ usage: GraphTokenUsage, message: String, node: String, model: String?, activityIds: [String], markedAsConfigured: Bool) -> GraphTokenUsage? {
        guard nodes[node] != nil else { return nil }
        let previous = messageUsage[message]
        guard previous != usage else { return nil }
        if previous == nil {
            messageOrder.append(message)
            if messageOrder.count > 1_024 { messageUsage.removeValue(forKey: messageOrder.removeFirst()) }
            if let m = model { messageModel[message] = m }
            messageActivityIds[message] = activityIds
            messageConfigured[message] = markedAsConfigured
            if nodeResponseOrder[node] == nil { nodeResponseOrder[node] = [] }
            nodeResponseOrder[node]!.append(message)
        }
        messageUsage[message] = usage
        return previous ?? GraphTokenUsage()
    }

    private func buildResponseRecords(for nodeId: String) -> [GraphResponseRecord] {
        guard let order = nodeResponseOrder[nodeId] else { return [] }
        return order.compactMap { msgId -> GraphResponseRecord? in
            guard let usage = messageUsage[msgId] else { return nil }
            return GraphResponseRecord(responseId: msgId, model: messageModel[msgId], usage: usage, activityIds: messageActivityIds[msgId] ?? [], markedAsConfigured: messageConfigured[msgId] ?? false)
        }
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
        // Where the run writes its output (the rest of the line: a path may hold spaces); a notification's <output-file> may move it.
        if let match = text.firstMatch(of: #/Output is being written to: (?<path>[^\r\n]+)/#) {
            var path = match.path.trimmingCharacters(in: .whitespaces)
            if path.hasSuffix(".") { path.removeLast() }
            if !path.isEmpty { update(id) { $0.outputFile = path } }
        }
        if !text.isEmpty { append(LogEntry(id: ExecutionGraphSupport.identifier(runID, id + ":launch"), kind: "system", text: text, provider: provider), to: id) }
    }
    /// A background Agent's launch result names its agent ID, which its
    /// notification's `task-id` and its hand-back's `from` carry.
    private func rememberAgentID(_ id: String, block: [String: Any]) {
        guard let text = Self.text(block["content"]), let match = text.firstMatch(of: #/agentId: (?<agent>[A-Za-z0-9_-]+)/#) else { return }
        let agent = String(match.agent)
        if taskAliases[agent] != nil || taskAliases.count < 512 { taskAliases[agent] = id }
    }
    private static func tag(_ body: Substring, _ name: String) -> String? {
        guard let start = body.range(of: "<\(name)>"), let end = body.range(of: "</\(name)>", range: start.upperBound..<body.endIndex) else { return nil }
        let value = body[start.upperBound..<end.lowerBound].trimmingCharacters(in: .whitespacesAndNewlines)
        return value.isEmpty ? nil : value
    }
    private func settleTasks(in text: String, notice: Bool) {
        var remaining = text[...]
        while let start = remaining.range(of: "<task-notification>"), let end = remaining.range(of: "</task-notification>", range: start.upperBound..<remaining.endIndex) {
            let body = remaining[start.upperBound..<end.lowerBound]
            remaining = remaining[end.upperBound...]
            let byTool = Self.tag(body, "tool-use-id").map { ExecutionGraphSupport.agentNodeID(runId: runID, toolUseId: $0) }.flatMap { nodes[$0] != nil ? $0 : nil }
            guard let id = byTool ?? Self.tag(body, "task-id").flatMap({ taskAliases[$0] }), let kind = nodes[id]?.kind, ["task", "agent"].contains(kind) else {
                // Work this run never started (an earlier process's) has no block to copy: its line names the turn's run.
                if notice { joinTurn(.work, name: nil, line: Self.tag(body, "summary") ?? Self.tag(body, "status")) }
                continue
            }
            let status = Self.tag(body, "status")?.lowercased() ?? "completed"
            let state = status == "completed" ? "completed" : ["failed", "error"].contains(status) ? "error" : "stopped"
            let summary = Self.tag(body, "summary")
            if kind == "agent" {
                // An agent's result is its report (`settleHandbacks`); the summary only fills an empty one until the report comes.
                let empty = nodes[id]?.output?.isEmpty ?? true
                update(id) { node in
                    if !ExecutionGraphSupport.terminal(node.state) { node.state = state }
                    if empty, let summary { node.output = summary }
                }
                if empty, summary != nil, let output = nodes[id]?.output { agentSummaries[id] = output }
                addNotice(for: id, root: notice)
            } else {
                let file = Self.tag(body, "output-file")
                update(id) { node in
                    if let file { node.outputFile = file }
                    guard !ExecutionGraphSupport.terminal(node.state) else { return }
                    node.state = state
                    if let summary { node.output = summary }
                }
                addNotice(for: id, root: notice)
            }
        }
    }
    /// A finished background block goes into a notice run (`BackgroundNoticeRuns`) as a copy. A root-level notice
    /// after the request's own turn opens the notice turn's run, which every new block until that turn's result
    /// joins and whose result is Claude's answer. A notice taken into a running turn (a nested agent's, or one in the
    /// request's or a steer's own turn) joins a run that is complete at once and has no result; the running turn's
    /// answer stays its own. A block copied before only updates its latest copy: a notice turn about such blocks
    /// alone answers in that earlier run again (`mergeTurn`), and one that also brings a new block opens a run for
    /// the new blocks and adds nothing to the earlier one.
    private func addNotice(for id: String, root: Bool) {
        guard provider == "claude", nodes[id] != nil else { return }
        let opensTurn = root && turnEnded && steeredTurn == nil
        if let earlier = copiedIn[id] {
            copy(id, into: earlier)
            if noticeTurn == nil, mergeTurn == nil, opensTurn {
                mergeTurn = earlier
                mergeRestore = nodes[ExecutionGraphSupport.mainNodeID(runId: earlier)]?.state
                setNoticeState(earlier, "running")
            }
            return
        }
        let run: String
        if let open = noticeTurn { run = open }
        else if opensTurn || mergeTurn != nil {
            guard let opened = openNoticeRun(running: true) else { return }
            // The turn brought new work after all: the earlier run gets nothing of it and ends as it was.
            if let merged = mergeTurn { setNoticeState(merged, mergeRestore ?? "completed"); mergeTurn = nil; mergeRestore = nil }
            noticeTurn = opened; run = opened
        } else if let open = absorbedRun { run = open }
        else {
            guard let opened = openNoticeRun(running: false) else { return }
            absorbedRun = opened; run = opened
        }
        copy(id, into: run)
    }
    /// Creates or updates block `id`'s copy in notice run `run`, and the run's request lines.
    private func copy(_ id: String, into run: String) {
        guard let block = nodes[id] else { return }
        let main = ExecutionGraphSupport.mainNodeID(runId: run)
        let joined = run + ":" + id
        let copyID = CoreValidation.identifier(joined) ? joined : ExecutionGraphSupport.identifier(run, "copy:" + id)
        if nodes[copyID] == nil {
            guard nodes.count < ExecutionGraphSupport.maximumNodes else { return }
            let copy = ExecutionGraphNode(id: copyID, runId: run, parentId: main, kind: block.kind, state: block.state, title: block.title,
                                          input: block.input, output: block.output, background: true, outputFile: block.outputFile, sourceBlockId: id)
            guard let normalized = ExecutionGraphSupport.normalized(copy) else { return }
            nodes[copyID] = normalized; order.append(copyID); emit(normalized)
            noticeCopies[run, default: []].append(copyID)
            // A run holding a copy is named after the work, no longer an agent's messages alone.
            messageAgents.removeValue(forKey: run)
            if copiedIn[id] != nil || copiedIn.count < 512 { copiedIn[id] = run }
        } else {
            update(copyID) { copy in
                copy.title = block.title; copy.input = block.input; copy.state = block.state
                copy.output = block.output; copy.outputFile = block.outputFile
            }
        }
        describe(run)
    }
    /// A notice run's title and request lines: its opener's lines, then each copied block and how it ended. A run
    /// holding copies is named after them; one without is named by what opened it.
    private func describe(_ run: String) {
        let copies = (noticeCopies[run] ?? []).compactMap { nodes[$0] }
        let opened: NoticeOpening = noticeOpeners[run] ?? (opener: .work, names: [], lines: [], senders: [])
        func name(_ copy: ExecutionGraphNode) -> String { copy.title.isEmpty ? copy.input ?? "" : copy.title }
        let lines = opened.lines + copies.map { copy in
            name(copy) + " · " + L(copy.state == "completed" ? "graph.block.taskNotice.completed" : copy.state == "error" ? "graph.block.taskNotice.failed" : "graph.block.taskNotice.stopped")
        }
        let title = copies.isEmpty ? BackgroundNoticeRuns.title(kind: BackgroundNoticeRuns.kind(opened.opener), names: opened.names)
                                   : BackgroundNoticeRuns.title(kind: BackgroundNoticeRuns.kind(.work), names: copies.map(name))
        update(ExecutionGraphSupport.mainNodeID(runId: run)) { $0.title = title; $0.input = lines.joined(separator: "\n") }
    }
    /// A root message about no block a notice run copies: a running agent's message, a message from a sender this run
    /// does not know, a notification for work this run never started, a delegation notice after the request's result.
    /// It joins the turn's open notice run, or opens that turn's run when it opens a turn; otherwise its line waits in
    /// `turnLines` for the turn's answer. One that brings neither a name nor a line (a notification without a summary
    /// or a status) opens nothing: an answer to it still opens a run of its own (the notice `result` branch). A message
    /// turn answers in an earlier 하위 에이전트 메시지 run again instead of opening one (`messageRun`). With no room for a
    /// run, the line waits for the answer, which finds a place of its own.
    private func joinTurn(_ opener: BackgroundNoticeRuns.Opener, name: String?, line: String?, agent: String? = nil) {
        guard provider == "claude" else { return }
        let trimmed = line?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        let held = HeldLine(line: trimmed.isEmpty ? nil : trimmed, name: name, agent: agent)
        guard held.name != nil || held.line != nil else { return }
        if let run = noticeTurn ?? mergeTurn {
            addOpening(run, [held])
        } else if turnEnded, steeredTurn == nil {
            let lines = Array((turnLines + [held]).prefix(Self.noticeLineLimit))
            turnLines = []
            if opener == .message, let run = messageRun(lines) {
                // The turn answers in that run again, as a turn about blocks it copied does (`mergeTurn`).
                mergeTurn = run
                mergeRestore = nodes[ExecutionGraphSupport.mainNodeID(runId: run)]?.state
                setNoticeState(run, "running")
                addOpening(run, lines)
            } else if let opened = openNoticeRun(running: true, opener: opener, names: Self.senders(lines).names, lines: lines.compactMap(\.line),
                                                 senders: Self.senders(lines).senders, agent: Self.soleAgent(lines)) {
                noticeTurn = opened
            } else { turnLines = lines }
        } else if turnLines.count < Self.noticeLineLimit { turnLines.append(held) }
    }
    /// The 하위 에이전트 메시지 run a turn of these messages answers in again instead of opening its own: the latest notice run
    /// while it holds only messages of the one agent that sent all of them, or past `messageRunLimit` the latest message run.
    private func messageRun(_ lines: [HeldLine]) -> String? {
        guard let latest = latestMessageRun, nodes[ExecutionGraphSupport.mainNodeID(runId: latest)] != nil else { return nil }
        if let agent = Self.soleAgent(lines), messageAgents[latest] == agent, latestNoticeRun == latest { return latest }
        return messageRunCount >= Self.messageRunLimit ? latest : nil
    }
    /// The latest notice run opened, if any.
    private var latestNoticeRun: String? {
        guard noticeIndex > 0 else { return nil }
        let run = BackgroundNoticeRuns.runID(original: runID, index: noticeIndex)
        return nodes[ExecutionGraphSupport.mainNodeID(runId: run)] != nil ? run : nil
    }
    /// The one agent block that sent every one of these lines, if they all came from one.
    private static func soleAgent(_ lines: [HeldLine]) -> String? {
        guard let agent = lines.first?.agent, lines.allSatisfy({ $0.agent == agent }) else { return nil }
        return agent
    }
    /// The names these lines give a run and who each stands for: each sender once, by its agent block (or by its name when no
    /// block sent it, `senderKey`), at most `noticeLineLimit`. Two agents that share a title are two names.
    private static func senders(_ lines: [HeldLine]) -> (names: [String], senders: [String]) {
        var names: [String] = [], senders: [String] = []
        for held in lines {
            guard let name = held.name, let key = senderKey(held), !senders.contains(key), names.count < noticeLineLimit else { continue }
            names.append(name); senders.append(key)
        }
        return (names, senders)
    }
    /// Who a line's name stands for: the agent block that sent it, else the name itself.
    private static func senderKey(_ held: HeldLine) -> String? { held.agent ?? held.name }
    /// Lines and names that join notice run `run`'s request and title, each sender named once. A line from anyone but the
    /// agent whose messages alone the run holds ends that (`messageAgents`).
    private func addOpening(_ run: String, _ lines: [HeldLine]) {
        guard !lines.isEmpty else { return }
        var opened: NoticeOpening = noticeOpeners[run] ?? (opener: .work, names: [], lines: [], senders: [])
        for held in lines {
            if let name = held.name, let key = Self.senderKey(held), !opened.senders.contains(key), opened.names.count < Self.noticeLineLimit {
                opened.names.append(name); opened.senders.append(key)
            }
            if let line = held.line, opened.lines.count < Self.noticeLineLimit { opened.lines.append(line) }
            if held.agent == nil || messageAgents[run] != held.agent { messageAgents.removeValue(forKey: run) }
        }
        noticeOpeners[run] = opened; describe(run)
    }
    /// A notice answer that goes on in the earlier notice run `run`: its lines join the run's request, the answer its result
    /// as a new paragraph, and the run ends as the turn did.
    private func answerAgain(in run: String, _ lines: [HeldLine], answer: String, failed: Bool) {
        addOpening(run, lines)
        update(ExecutionGraphSupport.mainNodeID(runId: run)) { node in
            node.output = (node.output?.isEmpty ?? true) ? answer : node.output! + "\n\n" + answer
        }
        setNoticeState(run, failed ? "error" : "completed")
    }
    /// A notice run's main block running again for a turn that answers in it, or ended. Unlike `update`, an ended
    /// main may run again: the turn after it is new work in the same run.
    private func setNoticeState(_ run: String, _ state: String) {
        let main = ExecutionGraphSupport.mainNodeID(runId: run)
        guard !finished, var node = nodes[main], node.state != state else { return }
        node.state = state
        guard var normalized = ExecutionGraphSupport.normalized(node) else { return }
        normalized.updatedAt = mightyTimestamp()
        nodes[main] = normalized; emit(normalized)
    }
    /// The next notice run's main block: running until a notice turn's result, or complete at once for absorbed notices.
    /// `opener` and `names` title it until a copy joins (`senders`: who each name stands for); `lines` are its request; `agent`
    /// is the agent block whose messages alone a 하위 에이전트 메시지 run holds. A message run leaves `noticeHeadroom` nodes free
    /// unless `reserving` is false (its answer's last chance of a run of its own).
    private func openNoticeRun(running: Bool, opener: BackgroundNoticeRuns.Opener = .work, names: [String] = [], lines: [String] = [], senders: [String] = [],
                               agent: String? = nil, reserving: Bool = true) -> String? {
        let room = opener == .message && reserving ? ExecutionGraphSupport.maximumNodes - Self.noticeHeadroom : ExecutionGraphSupport.maximumNodes
        guard nodes.count < room else { return nil }
        let run = BackgroundNoticeRuns.runID(original: runID, index: noticeIndex + 1)
        let main = ExecutionGraphNode(id: ExecutionGraphSupport.mainNodeID(runId: run), runId: run, kind: "main",
                                      state: running ? "running" : "completed", title: BackgroundNoticeRuns.title(kind: BackgroundNoticeRuns.kind(opener), names: names),
                                      input: lines.isEmpty ? nil : lines.joined(separator: "\n"))
        guard let normalized = ExecutionGraphSupport.normalized(main) else { return nil }
        noticeIndex += 1
        noticeOpeners[run] = (opener: opener, names: names, lines: lines, senders: senders)
        if opener == .message {
            messageRunCount += 1; latestMessageRun = run
            if let agent { messageAgents[run] = agent }
        }
        nodes[normalized.id] = normalized; order.append(normalized.id); emit(normalized)
        return run
    }
    /// A notice turn or a steered turn ended: the next one starts with no notices.
    private func clearNoticeTurn() {
        steeredTurn = nil; noticeTurn = nil; absorbedRun = nil; mergeTurn = nil; mergeRestore = nil; turnLines = []
    }
    /// Messages a background sub-agent sent this session (`AgentHandback`). Its
    /// framed final report is its result and ends it; like the agent's
    /// notification it copies the block into a notice run (`addNotice`). A
    /// message it sent while still running joins its block's log and changes
    /// nothing else. `origin` is the event's: its `senderTaskId` is the agent ID
    /// of a lone message, and `handback: true` marks a lone one as the report.
    private func settleHandbacks(in text: String, origin: [String: Any]?, notice: Bool) {
        let messages = AgentHandback.parse(text)
        let lone = messages.count == 1
        let sender = lone ? Self.key(origin?["senderTaskId"]) : nil
        for message in messages {
            guard let id = taskAliases[message.from] ?? sender.flatMap({ taskAliases[$0] }) ?? agentNames[message.from],
                  let node = nodes[id], node.kind == "agent" else {
                // A sender this run does not know: its message is the turn's, in a 하위 에이전트 메시지 run without a name.
                if notice, !message.body.isEmpty { joinTurn(.message, name: nil, line: message.body) }
                continue
            }
            guard message.isHandback || (lone && origin?["handback"] as? Bool == true) else {
                if !message.body.isEmpty {
                    append(LogEntry(id: ExecutionGraphSupport.identifier(runID, id + ":peer:" + message.body), kind: "assistant", text: message.body, provider: provider), to: id)
                    // A running agent's message opens (or joins) the turn's 하위 에이전트 메시지 run, named after the agent.
                    if notice { joinTurn(.message, name: node.title.isEmpty ? nil : node.title, line: message.body, agent: id) }
                }
                continue
            }
            let terminal = ExecutionGraphSupport.terminal(node.state)
            // A block that already ended keeps its state; its result takes the report only when empty or the notification's summary.
            let replaceable = !terminal || (node.output?.isEmpty ?? true) || node.output == agentSummaries[id]
            update(id) { node in
                if !terminal { node.state = "completed" }
                if replaceable, !message.body.isEmpty { node.output = message.body }
            }
            if replaceable, !message.body.isEmpty { agentSummaries.removeValue(forKey: id) }
            addNotice(for: id, root: notice)
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
    /// Pictures a tool or message returned. A child's tool's pictures join
    /// that child's block and return true; the request's own go to the
    /// transcript (false). A child not yet bound to a block keeps its pictures
    /// out of main logs. When the tool's owner is unknown (evicted, or never
    /// seen) the line's spawning tool names the child instead, so a child's
    /// picture never falls into the main transcript.
    func images(_ entry: LogEntry, toolID: String?, parentToolID: String? = nil) -> Bool {
        guard !finished else { return false }
        if let owner = toolID.flatMap({ toolOwners[$0] }), owner != .node(mainID) {
            guard let id = nodeID(owner) else { return true }
            if id != mainID { append(entry, to: id); return true }
        }
        if let parentToolID {
            let child = ExecutionGraphSupport.agentNodeID(runId: runID, toolUseId: parentToolID)
            if nodes[child] != nil { append(entry, to: child) }
            return true
        }
        return false
    }
    private func boundedPending(_ pending: PendingAgent, agent: String) -> PendingAgent {
        let metadata = pending.metadata
        let temporary = ExecutionGraphNode(id: ExecutionGraphSupport.identifier(runID, "pending:" + agent), runId: runID,
            kind: "agent", state: "running", title: metadata?.name ?? metadata?.agentType ?? L("graph.block.agent"),
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
            let node = ExecutionGraphNode(id: id, runId: runID, kind: "agent", state: "running", title: L("graph.block.agent"))
            nodes[id] = node; order.append(id); emit(node)
            aliases[agent] = id
            for activity in pending.activities { appendActivity(activity, to: id) }
            if let metadata = pending.metadata { apply(metadata, to: id) }
        }
        pendingAgents.removeAll()
        for (id, agent) in pendingParents { if let parent = aliases[agent], id != parent { update(id) { $0.parentId = parent } } }
        for id in order where id != mainID {
            guard let node = nodes[id], !ExecutionGraphSupport.terminal(node.state) else { continue }
            // A notice turn the process ended before its answer ends with the request.
            if node.kind == "main" { update(id) { $0.state = state }; continue }
            // Process success does not prove that an unfinished background
            // agent succeeded or returned an answer.
            update(id) { value in
                value.state = state == "error" ? "error" : "stopped"
                // A steer delegation sent, or a task's notification, says so in its own words (MightyGraphSupport.isDelegationNotice, isTaskNotice).
                let steer = MightyGraphSupport.isDelegationNotice(value.input ?? "") ? L("graph.block.unfinished.delegationNotice")
                    : MightyGraphSupport.isTaskNotice(value.input ?? "") ? L("graph.block.unfinished.taskNotice") : L("graph.block.unfinished.steer")
                let text = value.kind == "task" ? L("graph.block.unfinished.task")
                    : value.kind == "steer" ? steer : value.kind == "question" ? L("graph.block.unfinished.question") : L("graph.block.unfinished.agent")
                value.entries.append(LogEntry(id: ExecutionGraphSupport.identifier(runID, id + ":unfinished"), kind: "system", text: text, provider: provider))
            }
        }
        update(mainID) { $0.state = state }
        finished = true
    }
}
