import Foundation

public struct MightyGraphAgent: Codable, Sendable, Equatable, Identifiable {
    public var id: String
    public var parentID: String?
    public var title: String
    public var input: String
    public var status: String
    public var entries: [LogEntry]
    public init(id: String, parentID: String? = nil, title: String = "서브에이전트", input: String = "", status: String = "running", entries: [LogEntry] = []) {
        self.id = id; self.parentID = parentID; self.title = title; self.input = input; self.status = status; self.entries = entries
    }
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
    public init(id: String, input: String = "", status: String = "running", rootEntries: [LogEntry] = [], agents: [MightyGraphAgent] = [], resultEntries: [LogEntry] = [], sourceRunID: String? = nil, finalOutput: String? = nil) {
        self.id = id; self.input = input; self.status = status; self.rootEntries = rootEntries; self.agents = agents; self.resultEntries = resultEntries; self.sourceRunID = sourceRunID; self.finalOutput = finalOutput
    }
    public var settled: Bool { MightyGraphSupport.terminal(status) && agents.allSatisfy { MightyGraphSupport.terminal($0.status) } }

    mutating func refreshResult() {
        guard status == "completed", settled, let text = finalOutput, !text.isEmpty else { resultEntries = []; return }
        let timestamp = resultEntries.first?.timestamp ?? rootEntries.last?.timestamp ?? mightyTimestamp()
        let entry = LogEntry(id: id + "-result", kind: "assistant", text: text, timestamp: timestamp, provider: "claude")
        resultEntries = [entry]
        // Keep the final answer in the main block as well as the result block.
        if !rootEntries.contains(where: { $0.kind == "assistant" && $0.text == text }) {
            if let index = rootEntries.firstIndex(where: { $0.id == entry.id }) { rootEntries[index] = entry }
            else { rootEntries.append(entry) }
        }
    }
}

public enum MightyGraphSupport {
    public static func terminal(_ status: String) -> Bool { ["completed", "error", "stopped"].contains(status) }
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
                runs.append(MightyGraphRun(id: entry.id, input: entry.text))
            } else {
                if runs.isEmpty { runs.append(MightyGraphRun(id: "history-" + session.id, input: "", status: "completed")) }
                runs[runs.count - 1].rootEntries.append(entry)
            }
        }
        if !runs.isEmpty { finishLegacy(&runs[runs.count - 1], status: session.status == "idle" ? "completed" : session.status) }
        return runs
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
    public static func normalized(_ values: [MightyGraphRun], restoring: Bool, budget: inout Int) -> [MightyGraphRun] {
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

    public static func boundedLiveHistory(_ values: [MightyGraphRun]) -> [MightyGraphRun] {
        let estimate = values.reduce(0) { total, run in
            total + 512 + run.input.utf8.count + (run.finalOutput?.utf8.count ?? 0) * 3
                + entryBytes(run.rootEntries) + run.agents.reduce(0) { $0 + 768 + $1.title.utf8.count + $1.input.utf8.count + entryBytes($1.entries) }
        }
        guard estimate > 2 * 1024 * 1024 else { return values }
        var budget = 2 * 1024 * 1024
        return normalized(values, restoring: false, budget: &budget)
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
    public var mightyGraphRuns: [MightyGraphRun] { graphRuns ?? MightyGraphSupport.legacyRuns(self) }

    public mutating func beginGraphRun(input: String, id: String = UUID().uuidString) {
        guard kind == "claude", provider == "claude" else { return }
        if graphRuns == nil { graphRuns = MightyGraphSupport.legacyRuns(self) }
        graphRuns?.append(MightyGraphRun(id: id, input: input))
        graphRuns = graphRuns.map { MightyGraphSupport.boundedLiveHistory(Array($0.suffix(128))) }
    }

    public mutating func recordGraph(_ event: RunEvent) {
        guard kind == "claude", provider == "claude", event.sessionId == id else { return }
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
                if let output = node.output, !output.isEmpty { runs[index].finalOutput = output }
            } else {
                let parent = node.parentId == ExecutionGraphSupport.mainNodeID(runId: node.runId) ? nil : node.parentId
                var agent = MightyGraphAgent(id: node.id, parentID: parent, title: node.title, input: node.input ?? "", status: node.state, entries: node.entries)
                if let output = node.output, !output.isEmpty, !agent.entries.contains(where: { $0.kind == "assistant" && $0.text == output }) {
                    agent.entries.append(LogEntry(id: node.id + "-answer", kind: "assistant", text: output, provider: "claude"))
                }
                if let position = runs[index].agents.firstIndex(where: { $0.id == node.id }) {
                    let previous = runs[index].agents[position]
                    // Late starts may enrich identity, but never reopen a node.
                    agent.status = MightyGraphSupport.nextState(previous.status, agent.status)
                    if agent.input.isEmpty { agent.input = previous.input }
                    if agent.entries.isEmpty { agent.entries = previous.entries }
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
        runs[index].refreshResult()
        graphRuns = MightyGraphSupport.boundedLiveHistory(runs)
    }
}
