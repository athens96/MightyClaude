import Foundation

public struct MightyGraphAgent: Codable, Sendable, Equatable, Identifiable {
    public var id: String
    public var parentID: String?
    public var title: String
    public var input: String
    public var status: String
    public var entries: [LogEntry]
    /// nil means a subagent; "task" is a backgrounded command block.
    public var kind: String?
    public var usage: GraphTokenUsage?
    public var activityGeneration: Int?
    public init(id: String, parentID: String? = nil, title: String = "서브에이전트", input: String = "", status: String = "running", entries: [LogEntry] = [], kind: String? = nil, usage: GraphTokenUsage? = nil, activityGeneration: Int? = nil) {
        self.id = id; self.parentID = parentID; self.title = title; self.input = input; self.status = status; self.entries = entries; self.kind = kind; self.usage = usage; self.activityGeneration = activityGeneration
    }
    public var isTask: Bool { kind == "task" }
    /// A mid-turn message the user sent to a running Claude request.
    public var isSteer: Bool { kind == "steer" }
    /// The CLI summarized its context mid-turn; the block is complete on arrival.
    public var isCompact: Bool { kind == "compact" }
    /// A question the agent asked the user (AskUserQuestion); the answer is its output.
    public var isQuestion: Bool { kind == "question" }
}

public struct MightyGraphRun: Codable, Sendable, Equatable, Identifiable {
    public var id: String
    public var input: String
    public var status: String
    public var rootEntries: [LogEntry]
    public var agents: [MightyGraphAgent]
    public var resultEntries: [LogEntry]
    public var sourceRunID: String?
    public var finalOutput: String?
    /// Tokens of the main block alone; `totalUsage` adds every child block.
    public var usage: GraphTokenUsage?
    public var provider: String?
    public init(id: String, input: String = "", status: String = "running", rootEntries: [LogEntry] = [], agents: [MightyGraphAgent] = [], resultEntries: [LogEntry] = [], sourceRunID: String? = nil, finalOutput: String? = nil, usage: GraphTokenUsage? = nil, provider: String? = nil) {
        self.id = id; self.input = input; self.status = status; self.rootEntries = rootEntries; self.agents = agents; self.resultEntries = resultEntries; self.sourceRunID = sourceRunID; self.finalOutput = finalOutput; self.usage = usage; self.provider = provider
    }
    public var totalUsage: GraphTokenUsage? {
        let sum = agents.reduce(usage ?? GraphTokenUsage()) { $0 + ($1.usage ?? GraphTokenUsage()) }
        return sum.isEmpty ? nil : sum
    }
    public var settled: Bool { MightyGraphSupport.terminal(status) && agents.allSatisfy { MightyGraphSupport.terminal($0.status) } }

    /// The owning session is authoritative, including pre-provider graph saves.
    mutating func applyProvider(_ value: String) {
        let value = ProviderOptions.normalizeProvider(value)
        provider = value
        func labelled(_ entries: [LogEntry]) -> [LogEntry] {
            entries.map { original in
                var entry = original
                entry.provider = value
                if entry.activity != nil { entry.activity?.provider = value }
                return entry
            }
        }
        rootEntries = labelled(rootEntries)
        resultEntries = labelled(resultEntries)
        for index in agents.indices { agents[index].entries = labelled(agents[index].entries) }
    }

    mutating func refreshResult() {
        guard status == "completed", settled, let text = finalOutput, !text.isEmpty else { resultEntries = []; return }
        let timestamp = resultEntries.first?.timestamp ?? rootEntries.last?.timestamp ?? mightyTimestamp()
        let entry = LogEntry(id: id + "-result", kind: "assistant", text: text, timestamp: timestamp, provider: provider ?? "claude")
        resultEntries = [entry]
        // Keep the final answer in the main block as well as the result block.
        if !rootEntries.contains(where: { $0.kind == "assistant" && $0.text == text }) {
            if let index = rootEntries.firstIndex(where: { $0.id == entry.id }) { rootEntries[index] = entry }
            else { rootEntries.append(entry) }
        }
    }
}

public enum MightyGraphSupport {
    /// AI panes whose runs produce a graph: Claude via stream-json + Mods,
    /// Codex via exec JSONL. Gemini's stream has no agent structure.
    public static let providers = ["claude", "codex"]
    public static func terminal(_ status: String) -> Bool { ["completed", "error", "stopped"].contains(status) }

    /// The child-block kinds the core records; anything else is a sub-agent.
    public static let childKinds = ["task", "steer", "compact", "question"]
    public static func blockKind(_ agent: MightyGraphAgent) -> String {
        guard let kind = agent.kind, childKinds.contains(kind) else { return "agent" }
        return kind
    }
    /// The title a child block carries. The Mac's graph card and the phone's
    /// Mighty view both read it here, so the two cannot drift apart.
    public static func blockTitle(_ agent: MightyGraphAgent) -> String {
        switch blockKind(agent) {
        case "steer": return "중간 요청"
        case "compact": return ContextCompaction.title
        case "question": return agent.title.isEmpty ? "질문" : agent.title
        case "task": return agent.title.isEmpty ? "백그라운드 작업" : agent.title
        default: return agent.title.isEmpty ? "하위 에이전트" : agent.title
        }
    }
    static func nextState(_ previous: String, _ incoming: String) -> String {
        if ["error", "stopped"].contains(previous) { return previous }
        if ["error", "stopped"].contains(incoming) { return incoming }
        if previous == "completed" { return previous }
        return incoming
    }
    private static let states = ["idle", "running", "waiting", "completed", "error", "stopped"]

    /// Old profiles have only a bounded transcript. Reconstruct request groups,
    /// without inventing subagent identities or unseen child prompts.
    public static func legacyRuns(_ session: RunSession) -> [MightyGraphRun] {
        var runs: [MightyGraphRun] = []
        for entry in session.logs {
            if entry.kind == "user" {
                if !runs.isEmpty { finishLegacy(&runs[runs.count - 1], status: "completed") }
                runs.append(MightyGraphRun(id: entry.id, input: entry.text, provider: session.provider))
            } else {
                if runs.isEmpty { runs.append(MightyGraphRun(id: "history-" + session.id, input: "", status: "completed", provider: session.provider)) }
                runs[runs.count - 1].rootEntries.append(entry)
            }
        }
        if !runs.isEmpty { finishLegacy(&runs[runs.count - 1], status: session.status == "idle" ? "completed" : session.status) }
        return runs.map { original in
            var run = original; run.applyProvider(session.provider); return run
        }
    }
    private static func finishLegacy(_ run: inout MightyGraphRun, status: String) {
        run.status = status
        if status == "completed" {
            run.finalOutput = run.rootEntries.last(where: { $0.kind == "assistant" })?.text
            run.refreshResult()
        }
    }

    /// Share a bounded persistence budget with the rest of the saved profile.
    /// Malformed graph metadata cannot invalidate the original transcript.
    public static func normalized(_ values: [MightyGraphRun], restoring: Bool, budget: inout Int, provider: String? = nil) -> [MightyGraphRun] {
        var ids = Set<String>()
        var result: [MightyGraphRun] = []
        for var run in values.suffix(128).reversed() {
            var agentIDs = Set<String>()
            let agents = run.agents.prefix(128).filter { CoreValidation.identifier($0.id) && agentIDs.insert($0.id).inserted }
            // Reserve the whole tree before spending text bytes: clipping an
            // active child's text must never remove its unfinished identity.
            let overhead = 512 + run.id.utf8.count + (run.sourceRunID?.utf8.count ?? 0)
                + agents.reduce(0) { $0 + 512 + $1.id.utf8.count + ($1.parentID?.utf8.count ?? 0) }
            guard budget >= overhead, CoreValidation.identifier(run.id), ids.insert(run.id).inserted else { continue }
            budget -= overhead
            run.input = bounded(run.input, maximum: 32_768, budget: &budget)
            run.status = states.contains(run.status) ? run.status : "stopped"
            run.usage = run.usage?.normalized
            let resolvedProvider = provider ?? run.provider
                ?? run.rootEntries.compactMap(\.provider).first(where: ProviderOptions.ids.contains) ?? "claude"
            run.provider = ProviderOptions.normalizeProvider(resolvedProvider)
            if restoring && !terminal(run.status) { run.status = "stopped" }
            if let source = run.sourceRunID, !CoreValidation.identifier(source) { run.sourceRunID = nil }
            // Prefer the actual final answer over older intermediate output.
            run.finalOutput = run.finalOutput.map { bounded($0, maximum: min(32_768, max(0, budget / 3)), budget: &budget) }
            budget -= (run.finalOutput?.utf8.count ?? 0) * 2
            run.rootEntries = limitedEntries(run.rootEntries, maximum: 131_072, restoring: restoring, budget: &budget)
            run.agents = agents.map { original in
                var agent = original
                agent.title = bounded(agent.title, maximum: 240, budget: &budget)
                agent.input = bounded(agent.input, maximum: 16_384, budget: &budget)
                agent.status = states.contains(agent.status) ? agent.status : "stopped"
                if !["task", "steer", "compact", "question"].contains(agent.kind ?? "") { agent.kind = nil }
                agent.usage = agent.usage?.normalized
                agent.activityGeneration = ExecutionGraphSupport.normalizedGeneration(agent.activityGeneration)
                if restoring && !terminal(agent.status) { agent.status = "stopped" }
                agent.entries = limitedEntries(agent.entries, maximum: 65_536, restoring: restoring, budget: &budget)
                return agent
            }
            // Break malformed cycles and keep orphaned observations readable.
            let parents = Dictionary(uniqueKeysWithValues: run.agents.map { ($0.id, $0.parentID) })
            for index in run.agents.indices {
                let id = run.agents[index].id
                var visited: Set<String> = [id]
                var current = run.agents[index].parentID
                while let parent = current {
                    guard agentIDs.contains(parent), visited.insert(parent).inserted else { run.agents[index].parentID = nil; break }
                    current = parents[parent] ?? nil
                }
            }
            run.resultEntries = []
            run.applyProvider(run.provider ?? "claude")
            run.refreshResult()
            result.append(run)
        }
        return result.reversed()
    }
    private static func limitedEntries(_ values: [LogEntry], maximum: Int, restoring: Bool, budget: inout Int) -> [LogEntry] {
        let allowance = min(maximum, budget)
        var remaining = allowance
        let result = entries(values, restoring: restoring, budget: &remaining)
        budget -= allowance - remaining
        return result
    }

    /// The live history's byte budget, and the mark a trim goes down to. A trim
    /// re-aims the graph's camera, so it must be rare: stopping at the limit
    /// itself would leave the next streamed event over it again.
    public static let liveHistoryLimit = 2 * 1024 * 1024
    static let liveHistoryLowWater = liveHistoryLimit * 3 / 4
    /// The one measure both the trigger and the trim use. It counts everything
    /// `normalized` charges for and more, so a history under the low-water mark
    /// never loses a further run to that budget.
    static func liveHistoryBytes(_ values: [MightyGraphRun]) -> Int { values.reduce(0) { $0 + liveBytes($1) } }
    private static func liveBytes(_ run: MightyGraphRun) -> Int {
        512 + run.id.utf8.count + (run.sourceRunID?.utf8.count ?? 0) + run.input.utf8.count
            + (run.finalOutput?.utf8.count ?? 0) * 3 + entryBytes(run.rootEntries)
            + run.agents.reduce(0) { $0 + 768 + $1.id.utf8.count + ($1.parentID?.utf8.count ?? 0) + $1.title.utf8.count + $1.input.utf8.count + entryBytes($1.entries) }
    }

    public static func boundedLiveHistory(_ values: [MightyGraphRun]) -> [MightyGraphRun] {
        var sizes = values.map(liveBytes)
        var total = sizes.reduce(0, +)
        guard total > liveHistoryLimit else { return values }
        // Whole oldest runs first, down to the low-water mark, so the appends
        // that follow stay under the limit instead of trimming on every event.
        // The newest request is the one the user is watching: it never goes.
        var kept = values
        while total > liveHistoryLowWater, kept.count > 1 {
            total -= sizes.removeFirst()
            kept.removeFirst()
        }
        guard total > liveHistoryLimit else { return kept }
        // One run larger than the whole budget on its own: keep it and clip its
        // text rather than lose the request being watched.
        var budget = liveHistoryLimit
        return normalized(kept, restoring: false, budget: &budget)
    }
    private static func entryBytes(_ values: [LogEntry]) -> Int {
        values.reduce(0) { $0 + 512 + $1.text.utf8.count + ($1.activity?.output?.utf8.count ?? 0) + ($1.activity?.summary.utf8.count ?? 0) }
    }
    private static func bounded(_ text: String, maximum: Int, budget: inout Int) -> String {
        let value = ActivitySupport.clean(text, maximumBytes: min(maximum, max(0, budget)))
        budget -= value.utf8.count
        return value
    }
    private static func entries(_ values: [LogEntry], restoring: Bool, budget: inout Int) -> [LogEntry] {
        var ids = Set<String>()
        let result: [LogEntry] = values.suffix(100).reversed().compactMap { original in
            let overhead = 256 + original.id.utf8.count + original.timestamp.utf8.count
            guard budget >= overhead, CoreValidation.identifier(original.id), ids.insert(original.id).inserted,
                  ["user", "assistant", "system", "output", "error"].contains(original.kind) else { return nil }
            budget -= overhead
            var entry = original
            entry.text = bounded(entry.text, maximum: 32_768, budget: &budget)
            if let activity = entry.activity {
                entry.activity = ActivitySupport.normalized(activity, restoring: restoring)
                if var activity = entry.activity {
                    activity.summary = bounded(activity.summary, maximum: 1_000, budget: &budget)
                    activity.output = activity.output.map { bounded($0, maximum: 8_192, budget: &budget) }
                    entry.activity = activity
                }
            }
            return entry
        }
        return result.reversed()
    }
}

extension RunSession {
    public var mightyGraphRuns: [MightyGraphRun] {
        (graphRuns ?? MightyGraphSupport.legacyRuns(self)).map { original in
            var run = original; run.applyProvider(provider); return run
        }
    }

    public mutating func beginGraphRun(input: String, id: String = UUID().uuidString) {
        guard kind == "claude", MightyGraphSupport.providers.contains(provider) else { return }
        if graphRuns == nil { graphRuns = MightyGraphSupport.legacyRuns(self) }
        graphRuns?.append(MightyGraphRun(id: id, input: input, provider: provider))
        graphRuns = graphRuns.map { MightyGraphSupport.boundedLiveHistory(Array($0.suffix(128))) }
    }

    public mutating func recordGraph(_ event: RunEvent) {
        guard kind == "claude", MightyGraphSupport.providers.contains(provider), event.sessionId == id else { return }
        if event.type == "log", let entry = event.entry, entry.kind == "user" {
            beginGraphRun(input: entry.text, id: entry.id)
            return
        }
        guard var runs = graphRuns, !runs.isEmpty else { return }
        var index = runs.count - 1
        if event.type == "graph", let node = event.graph.flatMap({ ExecutionGraphSupport.normalized($0) }) {
            if let existing = runs.firstIndex(where: { $0.sourceRunID == node.runId }) { index = existing }
            else {
                // New process identities bind only to the pending request.
                guard runs[index].sourceRunID == nil, runs[index].status == "running" else { return }
                runs[index].sourceRunID = node.runId
            }
            if node.kind == "main" {
                runs[index].status = MightyGraphSupport.nextState(runs[index].status, node.state)
                if let usage = node.usage { runs[index].usage = usage }
                if let output = node.output, !output.isEmpty { runs[index].finalOutput = output }
            } else {
                let parent = node.parentId == ExecutionGraphSupport.mainNodeID(runId: node.runId) ? nil : node.parentId
                var agent = MightyGraphAgent(id: node.id, parentID: parent, title: node.title, input: node.input ?? "", status: node.state, entries: node.entries, kind: ["task", "steer", "compact", "question"].contains(node.kind) ? node.kind : nil, usage: node.usage, activityGeneration: node.activityGeneration)
                if let output = node.output, !output.isEmpty {
                    let answerID = provider == "codex"
                        ? ExecutionGraphSupport.identifier(node.runId, node.id + ":answer:\(node.activityGeneration ?? 0)")
                        : node.id + "-answer"
                    let hasAnswer = agent.entries.contains {
                        provider == "codex" ? $0.id == answerID : $0.kind == "assistant" && $0.text == output
                    }
                    if !hasAnswer { agent.entries.append(LogEntry(id: answerID, kind: "assistant", text: output, provider: provider)) }
                }
                if let position = runs[index].agents.firstIndex(where: { $0.id == node.id }) {
                    let previous = runs[index].agents[position]
                    let previousGeneration = previous.activityGeneration ?? 0
                    let incomingGeneration = agent.activityGeneration ?? 0
                    // New work must be explicit. Late snapshots from an older
                    // activity cannot replace current output or reopen history.
                    guard incomingGeneration >= previousGeneration else { return }
                    let newerActivity = incomingGeneration > previousGeneration
                    if newerActivity && runs[index].status != "running" { return }
                    if !(newerActivity && runs[index].status == "running") {
                        agent.status = MightyGraphSupport.nextState(previous.status, agent.status)
                    }
                    if agent.activityGeneration == nil { agent.activityGeneration = previous.activityGeneration }
                    if agent.kind == nil { agent.kind = previous.kind }
                    if agent.usage == nil { agent.usage = previous.usage }
                    if !previous.input.isEmpty { agent.input = previous.input }
                    var mergedEntries = previous.entries
                    for entry in agent.entries {
                        if let entryIndex = mergedEntries.firstIndex(where: { $0.id == entry.id }) { mergedEntries[entryIndex] = entry }
                        else { mergedEntries.append(entry) }
                    }
                    agent.entries = Array(mergedEntries.suffix(ExecutionGraphSupport.maximumEntries))
                    runs[index].agents[position] = agent
                } else if runs[index].agents.count < 128 { runs[index].agents.append(agent) }
            }
        } else if event.type == "log", let entry = event.entry {
            if let position = runs[index].rootEntries.firstIndex(where: { $0.id == entry.id }) { runs[index].rootEntries[position] = entry }
            else { runs[index].rootEntries.append(entry) }
            runs[index].rootEntries = Array(runs[index].rootEntries.suffix(100))
        } else if event.type == "status", let status = event.status {
            runs[index].status = MightyGraphSupport.nextState(runs[index].status, status)
            if ["error", "stopped"].contains(status) {
                for agent in runs[index].agents.indices where !MightyGraphSupport.terminal(runs[index].agents[agent].status) { runs[index].agents[agent].status = status }
            }
            // A legacy remote has no graph observation. Its real final log is
            // still available, but no child identities are guessed from text.
            if status == "completed", runs[index].sourceRunID == nil {
                runs[index].finalOutput = runs[index].rootEntries.last(where: { $0.kind == "assistant" })?.text
            }
        } else { return }
        runs[index].applyProvider(provider)
        runs[index].refreshResult()
        graphRuns = MightyGraphSupport.boundedLiveHistory(runs)
    }
}
