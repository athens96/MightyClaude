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
    /// Per-response attribution; absent in older graph events.
    public var responseRecords: [GraphResponseRecord]?
    /// Started in the background (a background sub-agent; every task block). See `ExecutionGraphNode`.
    public var background: Bool?
    /// A task block's output file: read for its last lines, never shown or sent to the phone.
    public var outputFile: String?
    /// Claude's answer in the turn this block's notice opened alone.
    public var noticeReply: String?
    /// A copy in a notice run: the finished background block it copies. See `ExecutionGraphNode`.
    public var sourceBlockId: String?
    public init(id: String, parentID: String? = nil, title: String = L("graph.agent.defaultTitle"), input: String = "", status: String = "running", entries: [LogEntry] = [], kind: String? = nil, usage: GraphTokenUsage? = nil, activityGeneration: Int? = nil, responseRecords: [GraphResponseRecord]? = nil,
                background: Bool? = nil, outputFile: String? = nil, noticeReply: String? = nil, sourceBlockId: String? = nil) {
        self.id = id; self.parentID = parentID; self.title = title; self.input = input; self.status = status; self.entries = entries; self.kind = kind; self.usage = usage; self.activityGeneration = activityGeneration; self.responseRecords = responseRecords
        self.background = background; self.outputFile = outputFile; self.noticeReply = noticeReply; self.sourceBlockId = sourceBlockId
    }
    public var isTask: Bool { kind == "task" }
    /// A sub-agent started with `run_in_background`.
    public var isBackgroundAgent: Bool { kind == nil && background == true }
    /// A mid-turn message the user sent to a running Claude request.
    public var isSteer: Bool { kind == "steer" }
    /// A steer that delegation sent, not the user (see `MightyGraphSupport.isDelegationNotice`).
    public var isDelegationNotice: Bool { isSteer && MightyGraphSupport.isDelegationNotice(input) }
    /// A steer the tracker made from a background task's notification (see `MightyGraphSupport.isTaskNotice`).
    public var isTaskNotice: Bool { isSteer && MightyGraphSupport.isTaskNotice(input) }
    /// A steer no person sent: a delegation notice or a task notice.
    public var isNotice: Bool { isDelegationNotice || isTaskNotice }
    /// What the block shows as its request: a task notice without its prefix.
    public var shownInput: String { isTaskNotice ? String(input.dropFirst(MightyGraphSupport.taskNoticePrefix.count)) : input }
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
    /// Per-response attribution for the main block; absent in older graph events.
    public var responseRecords: [GraphResponseRecord]?
    public var provider: String?
    /// The model label shown on this request node and projected to phone blocks.
    /// Computed once when the run starts and updated if the CLI later reports the actual
    /// model it used. nil means no label (CLI decided and we do not know which model).
    public var nodeModelLabel: String?
    /// The model the request was configured with when it started ("default" when the CLI
    /// decides). Kept so a later CLI report can recompute `nodeModelLabel`.
    public var configuredModel: String?
    /// Claude's reply to finished background work, not a request the user sent (`BackgroundNoticeRuns`); absent otherwise and in older saves.
    public var noticeRun: Bool?
    /// A notice run's title, "<kind> · <name>" (`BackgroundNoticeRuns.title(kind:names:)`), its main block's; absent in
    /// request runs and in older saves, whose notice runs read `BackgroundNoticeRuns.genericTitle` (`heading`).
    public var noticeTitle: String?
    /// `backgroundWaiting` of the run `shown` was made from, whose background blocks it no longer holds. Never saved.
    private var shownBackgroundWaiting: Int?
    private enum CodingKeys: String, CodingKey {
        case id, input, status, rootEntries, agents, resultEntries, sourceRunID, finalOutput, usage, responseRecords, provider, nodeModelLabel, configuredModel, noticeRun, noticeTitle
    }
    public init(id: String, input: String = "", status: String = "running", rootEntries: [LogEntry] = [], agents: [MightyGraphAgent] = [], resultEntries: [LogEntry] = [], sourceRunID: String? = nil, finalOutput: String? = nil, usage: GraphTokenUsage? = nil, responseRecords: [GraphResponseRecord]? = nil, provider: String? = nil, nodeModelLabel: String? = nil, configuredModel: String? = nil,
                noticeRun: Bool? = nil, noticeTitle: String? = nil) {
        self.id = id; self.input = input; self.status = status; self.rootEntries = rootEntries; self.agents = agents; self.resultEntries = resultEntries; self.sourceRunID = sourceRunID; self.finalOutput = finalOutput; self.usage = usage; self.responseRecords = responseRecords; self.provider = provider; self.nodeModelLabel = nodeModelLabel; self.configuredModel = configuredModel
        self.noticeRun = noticeRun; self.noticeTitle = noticeTitle
    }
    public var totalUsage: GraphTokenUsage? {
        let sum = agents.reduce(usage ?? GraphTokenUsage()) { $0 + ($1.usage ?? GraphTokenUsage()) }
        return sum.isEmpty ? nil : sum
    }
    /// Ended, and every block a view draws for it too: background work a request started runs on in the strip
    /// above the composer and is not waited for (`BackgroundNoticeRuns.shownAgents`).
    public var settled: Bool { MightyGraphSupport.terminal(status) && BackgroundNoticeRuns.shownAgents(self).allSatisfy { MightyGraphSupport.terminal($0.status) } }
    /// The run as views draw it: background blocks only inside a notice run (`BackgroundNoticeRuns.shownAgents`).
    /// It still says how much background work it waits on (`backgroundWaiting`), counted before the blocks went.
    public var shown: MightyGraphRun {
        let agents = BackgroundNoticeRuns.shownAgents(self)
        guard agents.count != self.agents.count else { return self }
        var run = self
        run.shownBackgroundWaiting = backgroundWaiting
        run.agents = agents
        return run
    }
    /// A notice run taken into a running turn: it opens complete and has no reply of its own. No view follows it or
    /// gives it a result card. A notice turn that failed or was stopped before its reply is not one: its card says so.
    public var quietNotice: Bool {
        noticeRun == true && status == "completed" && (finalOutput ?? "").trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }
    /// A notice run a later notice turn answers in again (`ExecutionGraphTracker`): it keeps showing its earlier reply meanwhile.
    public var reopenedWithReply: Bool {
        noticeRun == true && !MightyGraphSupport.terminal(status) && !(finalOutput ?? "").isEmpty
    }
    /// Background tasks and sub-agents still running after the request's own turn ended: it reads completed and waits on them.
    public var backgroundWaiting: Int {
        guard status == "completed" else { return 0 }
        return shownBackgroundWaiting ?? agents.filter { ($0.isTask || $0.isBackgroundAgent) && !MightyGraphSupport.terminal($0.status) }.count
    }

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

    /// A request's result shows once it settled; a notice run keeps its reply while a later turn answers in it again
    /// (`reopenedWithReply`) and after that turn failed or was stopped, so its card never loses the earlier answer.
    mutating func refreshResult() {
        guard (status == "completed" && settled) || reopenedWithReply || (noticeRun == true && ["error", "stopped"].contains(status)),
              let text = finalOutput, !text.isEmpty else { resultEntries = []; return }
        let timestamp = resultEntries.first?.timestamp ?? rootEntries.last?.timestamp ?? mightyTimestamp()
        let entry = LogEntry(id: id + "-result", kind: "assistant", text: text, timestamp: timestamp, provider: provider ?? "claude")
        resultEntries = [entry]
        // Keep the final answer in the main block as well as the result block. A result that goes on with notice
        // sections starts with the request's own answer, which the main block already shows.
        let own = text.components(separatedBy: MightyGraphSupport.noticeSectionMark)[0]
        if !rootEntries.contains(where: { $0.kind == "assistant" && ($0.text == text || $0.text == own) }) {
            if let index = rootEntries.firstIndex(where: { $0.id == entry.id }) { rootEntries[index] = entry }
            else { rootEntries.append(entry) }
        }
    }
}

public enum MightyGraphSupport {
    /// AI panes whose runs produce a graph: Claude via stream-json + Mods,
    /// Codex via exec JSONL, Antigravity (`gemini`) via agy's stream-json and
    /// its sub-agents' transcripts.
    public static let providers = ["claude", "codex", "gemini"]
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
        case "steer": return isDelegationNotice(agent.input) ? L("graph.block.delegationNotice") : isTaskNotice(agent.input) ? L("graph.block.taskNotice") : L("graph.block.steer")
        case "compact": return ContextCompaction.title
        case "question": return agent.title.isEmpty ? L("graph.block.question") : agent.title
        case "task": return agent.title.isEmpty ? L("graph.block.task") : agent.title
        default: return agent.title.isEmpty ? L("graph.block.agent") : agent.title
        }
    }
    /// The kind line under a background block's head: a background sub-agent or a
    /// background run, waiting or ended. Nil for any other block.
    public static func backgroundLabel(_ agent: MightyGraphAgent) -> String? {
        guard agent.isTask || agent.isBackgroundAgent else { return nil }
        return backgroundLabel(run: agent.isTask, status: agent.status)
    }
    /// `run`: a background run (a task block, a `shell` task), else a background sub-agent.
    /// `status` is a block's or a `BackgroundTask`'s; one still in motion reads waiting.
    public static func backgroundLabel(run: Bool, status: String) -> String {
        let word: String? = switch status {
        case "completed": L("graph.block.taskNotice.completed")
        case "error", "failed": L("graph.block.taskNotice.failed")
        case "stopped", "cancelled", "interrupted": L("graph.block.taskNotice.stopped")
        case "unknown": L("styles.state.taskStatus.unknown")
        default: nil
        }
        guard let word else { return run ? L("graph.background.runWaiting") : L("graph.background.agentWaiting") }
        return run ? L("graph.background.runEnded", ["status": word]) : L("graph.background.agentEnded", ["status": word])
    }
    /// Delegation hands a child's notice, or a follow-up to a running child,
    /// into a running Claude turn as a steer (`DelegationDelivery`); its text
    /// opens with one of these. Such a block is a 위임 알림, not a 중간 요청.
    public static let delegationNoticePrefixes = ["[Mighty Claude notice ", "[Mighty Claude follow-up "]
    public static func isDelegationNotice(_ input: String) -> Bool {
        delegationNoticePrefixes.contains { input.hasPrefix($0) }
    }
    /// A background task's `<task-notification>` the tracker shows as a steer under
    /// main, its readable line after this prefix; the next root-level answer is its
    /// reply. Such a block is a 작업 알림, not a 중간 요청.
    public static let taskNoticePrefix = "[Mighty Claude task notice] "
    /// What opens a notice turn's section in a request's result.
    public static let noticeSectionMark = "\n\n---\n\n**"
    /// The result with a notice turn's answer after it, under a bold line naming the notice; the request's own answer stays first.
    public static func withNoticeSection(_ result: String?, header: String, answer: String) -> String {
        (result.map { $0.isEmpty ? "**" : $0 + noticeSectionMark } ?? "**") + header + "**\n\n" + answer
    }
    public static func isTaskNotice(_ input: String) -> Bool { input.hasPrefix(taskNoticePrefix) }
    /// The result card's title in every view and on the phone: how the request ended, or the result so far while
    /// background work it started still runs (`MightyGraphRun.backgroundWaiting`).
    public static func resultTitle(status: String, waiting: Int) -> String {
        switch status {
        case "error", "failed": L("graph.block.resultError")
        case "stopped", "cancelled", "interrupted": L("graph.block.resultStopped")
        default: waiting > 0 ? L("graph.block.resultSoFar", ["count": "\(waiting)"]) : L("graph.block.result")
        }
    }
    /// Whether `event` is the first sign of the run a "starting" request (`status`) waits
    /// for that is not itself a status: a log line of the run or a graph observation. A
    /// user's line begins a request of its own (`recordGraph`), so it is not one.
    public static func startedBy(_ event: RunEvent, status: String) -> Bool {
        guard status == "starting" else { return false }
        if event.type == "graph" { return true }
        return event.type == "log" && event.entry?.kind != "user"
    }

    static func nextState(_ previous: String, _ incoming: String) -> String {
        if ["error", "stopped"].contains(previous) { return previous }
        if ["error", "stopped"].contains(incoming) { return incoming }
        if previous == "completed" { return previous }
        return incoming
    }
    /// "starting": a request the Mac sent whose run has not answered yet (natural submit B1).
    private static let states = ["idle", "starting", "running", "waiting", "completed", "error", "stopped"]

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
            run.noticeRun = run.noticeRun == true ? true : nil
            run.noticeTitle = run.noticeRun == true ? run.noticeTitle.map { bounded($0, maximum: 240, budget: &budget) }.flatMap { $0.isEmpty ? nil : $0 } : nil
            // Prefer the actual final answer over older intermediate output.
            run.finalOutput = run.finalOutput.map { bounded($0, maximum: min(32_768, max(0, budget / 3)), budget: &budget) }
            budget -= (run.finalOutput?.utf8.count ?? 0) * 2
            run.rootEntries = limitedEntries(run.rootEntries, maximum: 131_072, restoring: restoring, budget: &budget)
            run.agents = agents.map { original in
                var agent = original
                agent.title = bounded(agent.title, maximum: 240, budget: &budget)
                agent.input = bounded(agent.input, maximum: 16_384, budget: &budget)
                agent.noticeReply = agent.noticeReply.map { bounded($0, maximum: 32_768, budget: &budget) }.flatMap { $0.isEmpty ? nil : $0 }
                agent.background = agent.background == true ? true : nil
                if let source = agent.sourceBlockId, !CoreValidation.identifier(source) { agent.sourceBlockId = nil }
                if let file = agent.outputFile, file.utf8.count > 4096 || file.contains(where: \.isNewline) { agent.outputFile = nil }
                budget -= agent.outputFile?.utf8.count ?? 0
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
        values.reduce(0) { $0 + 512 + $1.text.utf8.count + ($1.activity?.output?.utf8.count ?? 0) + ($1.activity?.summary.utf8.count ?? 0) + AgentImageSupport.approximateBytes($1.images) }
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
                  LogEntryKinds.stored.contains(original.kind) else { return nil }
            budget -= overhead
            var entry = original
            entry.images = entry.kind == "image" ? AgentImageSupport.normalized(entry.images) : nil
            budget -= AgentImageSupport.approximateBytes(entry.images)
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

extension MightyGraphSupport {
    /// The runs the user asked for: every run but the notice runs (`BackgroundNoticeRuns`).
    public static func requestRuns(_ runs: [MightyGraphRun]) -> [MightyGraphRun] { runs.filter { $0.noticeRun != true } }
    /// The user's newest request. A notice run may come after it, even while it still runs.
    public static func newestRequestRun(_ runs: [MightyGraphRun]) -> MightyGraphRun? { newestRequestIndex(runs).map { runs[$0] } }
    /// Where the user's newest request is in `runs`: the one rule every "newest request" reader uses.
    public static func newestRequestIndex(_ runs: [MightyGraphRun]) -> Int? { runs.lastIndex { $0.noticeRun != true } }
    /// Whether Claude is answering finished background work now: a notice run after the user's newest request
    /// (`BackgroundNoticeRuns`) has not ended (its own notice turn, or an earlier one it answers in again). A steer then would
    /// open its turn inside that one, whose result would end both and leave the steer's answer to replace the request's:
    /// 바로 실행 waits for its turn instead (Windows `NoticeTurnRunning`).
    public static func noticeTurnRunning(_ runs: [MightyGraphRun]) -> Bool {
        let newest = newestRequestIndex(runs) ?? -1
        return runs[(newest + 1)...].contains { $0.noticeRun == true && !terminal($0.status) }
    }
    /// A notice turn ran before an event and no longer does after it: what waited for that turn (delegation's items, which
    /// no steer took meanwhile) is offered again now, not only at the run's end (Windows `NoticeTurnEnded`).
    public static func noticeTurnEnded(before: [MightyGraphRun], after: [MightyGraphRun]) -> Bool {
        noticeTurnRunning(before) && !noticeTurnRunning(after)
    }
    /// The run the views follow as the newest work (natural submit's not-own rule): the newest request, or a notice
    /// run after it that is a turn of its own — running, or holding Claude's reply. A notice taken into a running turn
    /// (`MightyGraphRun.quietNotice`) never takes the request's place.
    public static func followRunIndex(_ runs: [MightyGraphRun]) -> Int? {
        let newest = newestRequestIndex(runs)
        return runs.indices.last { index in
            index == newest || (runs[index].noticeRun == true && index > (newest ?? -1) && !runs[index].quietNotice)
        }
    }
    /// Each run's request number. Notice runs add nothing to the count and carry the number of the request
    /// before them (at least 1), so a reader that shows a number never shows 0.
    public static func requestOrdinals(_ runs: [MightyGraphRun]) -> [Int] {
        var count = 0
        return runs.map { run in
            if run.noticeRun != true { count += 1 }
            return max(1, count)
        }
    }
    /// The runs as views draw them (`MightyGraphRun.shown`).
    public static func shownRuns(_ runs: [MightyGraphRun]) -> [MightyGraphRun] {
        guard runs.contains(where: { $0.agents.contains { $0.background == true } }) else { return runs }
        return runs.map(\.shown)
    }
    /// The run the process's lines, statuses and usage belong to (`RunSession.recordGraph`): a notice run that ended
    /// takes none (they are the request's before it), and a running notice turn's never reach a request still waiting
    /// for its own run.
    static func routedIndex(_ runs: [MightyGraphRun]) -> Int {
        var index = runs.lastIndex { $0.noticeRun != true || !terminal($0.status) } ?? runs.count - 1
        if runs[index].sourceRunID == nil, runs[index].noticeRun != true,
           let notice = runs.lastIndex(where: { $0.noticeRun == true && !terminal($0.status) }) { index = notice }
        return index
    }
    /// Whether `event` is the newest request's own: a log, status or usage line routed to it, or a graph observation
    /// that is neither a notice run's nor a run bound before. `AppStore.apply` ends 전송 중 and `recordGraph` moves
    /// the request out of "starting" by this one rule. True when there is no run to tell by.
    public static func newestRequestOwns(_ event: RunEvent, runs: [MightyGraphRun]) -> Bool {
        guard !runs.isEmpty, let newest = newestRequestIndex(runs) else { return true }
        guard event.type == "graph" else { return routedIndex(runs) == newest }
        guard let node = event.graph else { return true }
        return !BackgroundNoticeRuns.isNoticeRun(node.runId) && !runs[..<newest].contains { $0.sourceRunID == node.runId }
    }
}

extension RunSession {
    /// The pane as readers of "the user's requests" see it (a style's phase and plan stage): without notice runs.
    public var withoutNoticeRuns: RunSession {
        guard graphRuns?.contains(where: { $0.noticeRun == true }) == true else { return self }
        var session = self
        session.graphRuns = graphRuns.map(MightyGraphSupport.requestRuns)
        return session
    }

    public var mightyGraphRuns: [MightyGraphRun] {
        (graphRuns ?? MightyGraphSupport.legacyRuns(self)).map { original in
            var run = original; run.applyProvider(provider); return run
        }
    }

    /// `configuredModel` is the model the request resolved to before it started
    /// (explicit choice, mode default, or "default" when the CLI decides). It has no
    /// default value on purpose: every production caller must pass the resolution.
    /// `status` "starting" is a request this app sent that reads 전송 중 until its run's
    /// first event (natural submit B1; `recordGraph` moves it on).
    public mutating func beginGraphRun(input: String, id: String = UUID().uuidString, configuredModel: String, status: String = "running") {
        guard kind == "claude", MightyGraphSupport.providers.contains(provider) else { return }
        if graphRuns == nil { graphRuns = MightyGraphSupport.legacyRuns(self) }
        graphRuns?.append(MightyGraphRun(id: id, input: input, status: status, provider: provider,
                                         nodeModelLabel: GraphModelLabel.nodeModelLabel(cliReportedModel: nil, configuredModel: configuredModel),
                                         configuredModel: configuredModel))
        graphRuns = graphRuns.map { MightyGraphSupport.boundedLiveHistory(Array($0.suffix(128))) }
    }

    public mutating func recordGraph(_ event: RunEvent) {
        guard kind == "claude", MightyGraphSupport.providers.contains(provider), event.sessionId == id else { return }
        if event.type == "log", let entry = event.entry, entry.kind == "user" {
            beginGraphRun(input: entry.text, id: entry.id, configuredModel: model)
            return
        }
        guard var runs = graphRuns, !runs.isEmpty else { return }
        // A sent request reads "starting" until its run answers: a status moves it on as
        // any status does (to running, or to the error or stop of a failed start); a log
        // or a graph observation of the run means it runs.
        // Written back at once: a path below that returns early (a graph observation that
        // does not normalize, a stale generation) never leaves 전송 중 after the app's own
        // submit state (`AppStore.apply`, the same `newestRequestOwns` and `startedBy`) moved on.
        var index = MightyGraphSupport.routedIndex(runs)
        // Only the request's own run starts it: never a notice turn's lines or an earlier run's blocks.
        // A status routed to it moves it below, as any status does.
        if let newest = MightyGraphSupport.newestRequestIndex(runs), MightyGraphSupport.newestRequestOwns(event, runs: runs),
           MightyGraphSupport.startedBy(event, status: runs[newest].status) {
            runs[newest].status = "running"
            graphRuns = runs
        }
        if event.type == "usage" {
            // The CLI reported the model it actually used: it outranks the configured name.
            guard let reported = event.usage?.model, !reported.isEmpty else { return }
            runs[index].nodeModelLabel = GraphModelLabel.nodeModelLabel(cliReportedModel: reported, configuredModel: runs[index].configuredModel ?? "default")
            graphRuns = runs
            return
        }
        if event.type == "graph", let node = event.graph.flatMap({ ExecutionGraphSupport.normalized($0) }) {
            if let existing = runs.firstIndex(where: { $0.sourceRunID == node.runId }) { index = existing }
            else if BackgroundNoticeRuns.isNoticeRun(node.runId) {
                // A notice run opens with its main block, after the request whose work it reports on and before a
                // request still waiting for its own run, which stays the newest.
                guard node.kind == "main" else { return }
                let original = runs.last { $0.sourceRunID.map { node.runId.hasPrefix($0 + BackgroundNoticeRuns.marker) } == true }
                index = runs.count
                while index > 0, runs[index - 1].sourceRunID == nil, !MightyGraphSupport.terminal(runs[index - 1].status) { index -= 1 }
                runs.insert(MightyGraphRun(id: node.runId, input: node.input ?? "", status: node.state, sourceRunID: node.runId, provider: provider,
                                           nodeModelLabel: original?.nodeModelLabel, configuredModel: original?.configuredModel,
                                           noticeRun: true), at: index)
            } else {
                // New process identities bind only to the pending request.
                index = runs.count - 1
                guard runs[index].sourceRunID == nil, runs[index].status == "running" else { return }
                runs[index].sourceRunID = node.runId
            }
            if node.kind == "main" {
                // A notice run's request is the finished work its main block names.
                // Its state is its main block's: a later notice turn may answer in it again (`ExecutionGraphTracker`).
                if runs[index].noticeRun == true {
                    if let input = node.input { runs[index].input = input }
                    if !node.title.isEmpty { runs[index].noticeTitle = node.title }
                    runs[index].status = node.state
                } else {
                    runs[index].status = MightyGraphSupport.nextState(runs[index].status, node.state)
                }
                if let usage = node.usage { runs[index].usage = usage }
                if let records = node.responseRecords { runs[index].responseRecords = records }
                if let output = node.output, !output.isEmpty { runs[index].finalOutput = output }
            } else {
                let parent = node.parentId == ExecutionGraphSupport.mainNodeID(runId: node.runId) ? nil : node.parentId
                var agent = MightyGraphAgent(id: node.id, parentID: parent, title: node.title, input: node.input ?? "", status: node.state, entries: node.entries, kind: ["task", "steer", "compact", "question"].contains(node.kind) ? node.kind : nil, usage: node.usage, activityGeneration: node.activityGeneration, responseRecords: node.responseRecords,
                                              background: node.background, outputFile: node.outputFile, noticeReply: node.noticeReply, sourceBlockId: node.sourceBlockId)
                if let output = node.output, !output.isEmpty {
                    // Codex and Antigravity children take new assignments: one answer per generation.
                    let answerID = ["codex", "gemini"].contains(provider)
                        ? ExecutionGraphSupport.identifier(node.runId, node.id + ":answer:\(node.activityGeneration ?? 0)")
                        : node.id + "-answer"
                    let hasAnswer = agent.entries.contains {
                        provider == "codex" ? $0.id == answerID : $0.id == answerID || ($0.kind == "assistant" && $0.text == output)
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
                    // An Antigravity sub-agent whose helper reported back goes on with the same assignment.
                    let continuing = provider == "gemini" && previous.status == "completed" && agent.status == "running" && runs[index].status == "running"
                    if !(newerActivity && runs[index].status == "running") && !continuing {
                        agent.status = MightyGraphSupport.nextState(previous.status, agent.status)
                    }
                    if agent.activityGeneration == nil { agent.activityGeneration = previous.activityGeneration }
                    if agent.kind == nil { agent.kind = previous.kind }
                    if agent.usage == nil { agent.usage = previous.usage }
                    if agent.responseRecords == nil { agent.responseRecords = previous.responseRecords }
                    if agent.background == nil { agent.background = previous.background }
                    if agent.outputFile == nil { agent.outputFile = previous.outputFile }
                    if agent.noticeReply == nil { agent.noticeReply = previous.noticeReply }
                    if agent.sourceBlockId == nil { agent.sourceBlockId = previous.sourceBlockId }
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
            // A run without a graph observation still has its real final log,
            // but no child identities are guessed from text.
            if status == "completed", runs[index].sourceRunID == nil {
                runs[index].finalOutput = runs[index].rootEntries.last(where: { $0.kind == "assistant" })?.text
            }
        } else { return }
        runs[index].applyProvider(provider)
        runs[index].refreshResult()
        graphRuns = MightyGraphSupport.boundedLiveHistory(runs)
    }
}
