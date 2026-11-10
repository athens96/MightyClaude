import CoreGraphics
import Foundation

/// The advisor's band beside a run in the flow: the model a Claude session asks for advice while it works (Claude
/// Code's experimental advisor tool), each time it was asked, by whom, where in the caller's work and how it ended.
/// It reads the run's own record (`GraphAdvisor`, `MightyGraphRun.configuredAdvisor`) and says only what the app
/// saw: the advisor's reply usually goes to Claude encrypted, and then the band says so and shows no text.
public enum FlowAdvisor {
    /// The band's node suffix (`MightyGraphLayout.Content.advisor`).
    public static let suffix = "advisor"
    public static let width: CGFloat = 248
    /// Between the band and the leftmost block beside it.
    static let gap: CGFloat = 28
    public static let headerHeight = DesignMetrics.Layout.blockHead
    public static let rowHeight: CGFloat = 38
    public static let earlierHeight: CGFloat = 18
    public static let noteHeight: CGFloat = 32
    public static let footerHeight: CGFloat = 24
    /// A band never stands shorter than its header, the line that counts earlier consultations, one row, its note and
    /// its totals: its newest consultation always shows.
    public static let minimumHeight = headerHeight + earlierHeight + rowHeight + noteHeight + footerHeight

    /// What the advisor is doing in a run that still goes on.
    public enum State: Equatable, Sendable { case onCall, consulting }
    /// Where in the caller's work a consultation stands, as what the app saw: the caller had changed no file yet, it
    /// was in the middle of its work, or nothing but its answer followed.
    public enum Position: String, Equatable, Sendable { case beforeChange, during, lastStep }

    public struct Row: Equatable, Sendable, Identifiable {
        public var id: String
        /// Who asked: the main session or a sub-agent.
        public var role: FlowRole
        /// The caller in words: the main session's word, or the sub-agent's name.
        public var caller: String
        public var ink: FlowInk
        /// The diagram block that asked (`MightyGraphLayout.Node.id`).
        public var callerNodeID: String
        public var position: Position
        /// When it was asked, and how long the reply took once it came.
        public var started: Date?
        public var seconds: TimeInterval?
        /// The consultation's state (`GraphAdvisorCall.state`) and how it ended, in words.
        public var state: String
        public var outcome: String
        /// The advice, only when the CLI gave it as text.
        public var text: String?
        /// "답 직전 마지막 단계 · 조언 전달 · 내용 비표시".
        public var line: String { FlowAdvisor.words(position) + " · " + outcome }
    }

    public struct Band: Equatable, Sendable {
        /// The advisor's model: as a turn's result named it, else the one the request was started with, marked as
        /// asked for. Nil when neither is known.
        public var model: String?
        /// Nil once the run and everything it started ended.
        public var state: State?
        /// Every consultation the run keeps, oldest first.
        public var rows: [Row]
        /// Consultations seen before the ones kept.
        public var earlier: Int
        /// Every consultation seen.
        public var calls: Int
        /// The tokens of the advisor's own messages, once a turn's result named them.
        public var usage: GraphTokenUsage?
        /// A reply went to Claude without text to show.
        public var hidesText: Bool

        /// "조언자 · Opus 5.5 · 검토 중".
        public var header: String {
            var parts = [L("graph.identity.role.advisor")]
            if let model { parts.append(model) }
            if let state { parts.append(FlowAdvisor.words(state)) }
            return parts.joined(separator: " · ")
        }
        /// "호출 3 · 읽은 토큰 34K": what the advisor read is its input, cached or not.
        public var totals: String {
            var parts = [L("graph.advisor.totals.calls", ["count": "\(calls)"])]
            if let usage { parts.append(L("graph.advisor.totals.tokens", ["tokens": GraphTokenUsage.compact(usage.inputTokens + usage.cacheReadTokens + usage.cacheCreationTokens)])) }
            return parts.joined(separator: " · ")
        }
        public var note: String? { hidesText ? L("graph.advisor.note.hidden") : nil }
    }

    /// The band of `run`, nil for a run without an advisor: one that was started with none known to be on and never
    /// consulted one.
    public static func band(_ run: MightyGraphRun, definitions: [String: AgentDefinition] = [:]) -> Band? {
        guard shows(run) else { return nil }
        let advisor = run.advisor ?? GraphAdvisor()
        let agents = run.flowAgents
        let rows = advisor.calls.map { call -> Row in
            let agent = call.caller.flatMap { id in agents.first { $0.id == id } }
            let ended = agent.map { $0.status == "completed" } ?? (call.caller == nil && run.status == "completed")
            var seconds: TimeInterval?
            if let start = call.started, let end = call.ended, end >= start { seconds = end.timeIntervalSince(start) }
            return Row(id: call.id, role: agent == nil ? .lead : .subagent,
                       caller: agent.map(MightyGraphSupport.blockTitle) ?? L("graph.identity.role.lead"),
                       ink: agent.map { FlowRoles.ink(.subagent, color: $0.identity?.type.flatMap { definitions[$0]?.color }) } ?? FlowRoles.ink(.lead),
                       callerNodeID: MightyGraphLayout.nodeID(run, suffix: agent.map { "agent:" + $0.id } ?? MightyGraphLayout.workSuffix),
                       position: position(call, callerAnswered: ended), started: call.started, seconds: call.state == "consulting" ? nil : seconds,
                       state: call.state, outcome: outcome(call), text: call.text)
        }
        let live = !MightyGraphSupport.terminal(run.status) || agents.contains { $0.isSubagent && $0.sourceBlockId == nil && !MightyGraphSupport.terminal($0.status) }
        let consulting = advisor.calls.contains { $0.state == "consulting" }
        return Band(model: ModelUsageFormat.versionedNodeLabel(GraphModelLabel.agentModelLabel(resolved: advisor.model, requested: run.configuredAdvisor)),
                    state: consulting ? .consulting : live ? .onCall : nil, rows: rows, earlier: max(0, advisor.count - advisor.calls.count), calls: advisor.count,
                    usage: advisor.usage, hidesText: advisor.calls.contains { $0.state == "done" && $0.text == nil })
    }

    /// A run has a band when an advisor was known to be on when it started, or when its stream showed one at work.
    public static func shows(_ run: MightyGraphRun) -> Bool {
        (run.provider ?? "claude") == "claude" && (run.configuredAdvisor != nil || run.advisor != nil)
    }

    /// Where a consultation stands in its caller's work. `callerAnswered`: the caller ended with its answer.
    /// Nothing but the answer followed it: its last step. Else, asked before the caller changed any file, or in the
    /// middle of the work.
    public static func position(_ call: GraphAdvisorCall, callerAnswered: Bool) -> Position {
        if callerAnswered, call.followed != true { return .lastStep }
        return call.beforeChange == true ? .beforeChange : .during
    }

    public static func words(_ position: Position) -> String {
        switch position {
        case .beforeChange: L("graph.advisor.position.beforeChange")
        case .during: L("graph.advisor.position.during")
        case .lastStep: L("graph.advisor.position.lastStep")
        }
    }
    public static func words(_ state: State) -> String {
        switch state {
        case .onCall: L("graph.advisor.state.onCall")
        case .consulting: L("graph.advisor.state.consulting")
        }
    }

    /// How a consultation ended, in words. A reply without text went to Claude all the same: nothing is made up for it.
    public static func outcome(_ call: GraphAdvisorCall) -> String {
        switch call.state {
        case "done": call.text == nil ? L("graph.advisor.outcome.hidden") : L("graph.advisor.outcome.delivered")
        case "declined": L("graph.advisor.outcome.declined")
        case "error": call.errorCode.map { L("graph.advisor.outcome.errorCode", ["code": $0]) } ?? L("graph.advisor.outcome.error")
        case "stopped": L("graph.advisor.outcome.stopped")
        default: L("graph.advisor.state.consulting")
        }
    }

    /// The rows a band of `height` shows, the newest last, and how many consultations stand before them: the ones it
    /// has no room for and the ones no longer kept. A line says so in the rows' place when there are any.
    public static func visible(_ band: Band, height: CGFloat) -> (rows: [Row], earlier: Int) {
        let room = height - headerHeight - footerHeight - (band.hidesText ? noteHeight : 0)
        let whole = max(0, Int(room / rowHeight))
        if band.earlier == 0, band.rows.count <= whole { return (band.rows, 0) }
        let shown = max(0, Int((room - earlierHeight) / rowHeight))
        let rows = Array(band.rows.suffix(shown))
        return (rows, band.calls - rows.count)
    }

    /// The blocks that consulted the advisor in `run`, each once, in the order they first asked: where the band's
    /// dotted connectors go.
    public static func callers(_ run: MightyGraphRun) -> [String] {
        var seen = Set<String>(), result: [String] = []
        let agents = run.flowAgents
        for call in run.advisor?.calls ?? [] {
            let agent = call.caller.flatMap { id in agents.first { $0.id == id } }
            let node = MightyGraphLayout.nodeID(run, suffix: agent.map { "agent:" + $0.id } ?? MightyGraphLayout.workSuffix)
            if seen.insert(node).inserted { result.append(node) }
        }
        return result
    }

    // MARK: The session log

    /// The advisor in words for the log: its role and, when known, its model.
    public static func name(_ run: MightyGraphRun) -> String {
        let model = ModelUsageFormat.versionedNodeLabel(GraphModelLabel.agentModelLabel(resolved: run.advisor?.model, requested: run.configuredAdvisor))
        return ([L("graph.identity.role.advisor")] + [model].compactMap { $0 }).joined(separator: " · ")
    }

    /// What the advisor did in the pane's newest activity, for the log (`FlowLog.rows`): each consultation's start and,
    /// once it ended, its end. Of each run only the last `limit` consultations are read.
    public static func notes(_ runs: [MightyGraphRun], limit: Int = FlowLog.visibleRows) -> [FlowLog.AdvisorNote] {
        var notes: [FlowLog.AdvisorNote] = []
        for index in FlowLog.activity(runs) {
            let run = runs[index]
            guard let calls = run.advisor?.calls, !calls.isEmpty else { continue }
            let who = name(run), node = MightyGraphLayout.nodeID(run, suffix: suffix)
            let agents = run.flowAgents
            for call in calls.suffix(max(0, limit)) {
                let caller = call.caller.flatMap { id in agents.first { $0.id == id } }.map(MightyGraphSupport.blockTitle) ?? L("graph.identity.role.lead")
                if let started = call.startedAt {
                    notes.append(.init(id: node + ":" + call.id + ":start", time: started, who: who, what: L("graph.advisor.log.asked", ["caller": caller]), nodeID: node))
                }
                if call.state != "consulting", let ended = call.endedAt {
                    notes.append(.init(id: node + ":" + call.id + ":end", time: ended, who: who, what: outcome(call), nodeID: node))
                }
            }
        }
        return notes
    }

    /// The advisor's part of the status line (`FlowLog.status`): at work, on call while the activity goes on, or how
    /// often it was consulted once it ended. Nil for an activity without an advisor.
    public static func status(_ runs: [MightyGraphRun]) -> FlowLog.AdvisorState? {
        let span = FlowLog.activity(runs)
        let advised = span.map { runs[$0] }.filter(shows)
        guard !advised.isEmpty else { return nil }
        if advised.contains(where: { $0.advisor?.calls.contains { $0.state == "consulting" } == true }) { return .consulting }
        let followed = runs[MightyGraphSupport.followRunIndex(runs) ?? span.lowerBound]
        let waits = MightyGraphSupport.newestRequestIndex(runs).map { BackgroundNoticeRuns.awaitingNotice(runs[$0], in: runs) != nil } ?? false
        if !MightyGraphSupport.terminal(followed.status) || waits { return .onCall }
        let calls = advised.reduce(0) { $0 + ($1.advisor?.count ?? 0) }
        return calls > 0 ? .consulted(calls) : nil
    }
}
