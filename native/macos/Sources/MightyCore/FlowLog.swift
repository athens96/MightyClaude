import Foundation

/// The flow's session log: the last few things that happened in the pane's newest activity (the newest request
/// and Claude's replies to its background work after it), each with its time, who did it and what it was, under
/// one status line. It reads the very runs the timeline draws (`MightyTimeline.groups`) and their blocks' own
/// records: there is no second event source. A row is something the app saw happen at a time it recorded, never
/// a reconstruction.
public enum FlowLog {
    /// Who a row is about.
    public enum Actor: Equatable, Sendable {
        /// The main session.
        case lead
        /// A sub-agent, by its block's id.
        case subagent(String)
        /// The advisor a Claude session consulted.
        case advisor

        public var role: FlowRole {
            switch self {
            case .lead: .lead
            case .subagent: .subagent
            case .advisor: .advisor
            }
        }
    }

    public struct Row: Equatable, Sendable, Identifiable {
        public var id: String
        public var time: Date
        public var actor: Actor
        /// The actor in words: the main session's model, a sub-agent's name, the advisor.
        public var who: String
        /// The actor's ink: its role's, or a custom agent's own colour.
        public var ink: FlowInk
        public var what: String
        /// The diagram block the row is about (`MightyGraphLayout.Node.id`).
        public var nodeID: String

        public init(id: String, time: Date, actor: Actor, who: String, ink: FlowInk, what: String, nodeID: String) {
            self.id = id; self.time = time; self.actor = actor; self.who = who; self.ink = ink; self.what = what; self.nodeID = nodeID
        }
    }

    /// Something the advisor did, for the log: fed by the run's advisor calls. The strip words and inks it as it does
    /// every other row.
    public struct AdvisorNote: Equatable, Sendable {
        public var id: String
        /// When it happened, as the app recorded it.
        public var time: String
        /// The advisor in words (its model, else its role's name).
        public var who: String
        public var what: String
        /// The block that consulted it.
        public var nodeID: String
        public init(id: String, time: String, who: String, what: String, nodeID: String) {
            self.id = id; self.time = time; self.who = who; self.what = what; self.nodeID = nodeID
        }
    }

    /// The rows the strip shows when it is open.
    public static let visibleRows = 5
    /// What the diagram's own view keeps of the pane's flow before the strip shows its rows (Windows `flow.ts` LogDiagramRoom).
    public static let diagramRoom: Double = 160

    /// Whether the strip shows its rows in a flow `room` points high (the diagram's view and the strip under it), the strip's
    /// header being `head` and its rows `rows` high: only while the diagram still keeps `diagramRoom` beside them. A shorter pane
    /// shows the strip's status line alone, whatever the pane's fold, so the log never takes the diagram's whole height; an
    /// unmeasured flow (0) shows them (Windows `logRowsFit`).
    public static func rowsFit(room: Double, head: Double, rows: Double) -> Bool {
        room <= 0 || room >= head + rows + diagramRoom
    }
    /// The most a row's words hold; the view cuts what does not fit its width.
    static let lineBytes = 240

    /// The runs of the pane's newest activity: its newest request and the notice runs after it, by index into `runs`.
    public static func activity(_ runs: [MightyGraphRun]) -> Range<Int> {
        guard !runs.isEmpty else { return 0..<0 }
        return (MightyGraphSupport.newestRequestIndex(runs) ?? runs.count - 1)..<runs.count
    }

    /// The last `limit` rows of the newest activity, oldest first. `definitions` are the custom agents the blocks'
    /// types name (their colour is the row's ink); `advisor` is what the advisor did in that activity.
    ///
    /// Of each block only its last `limit` events are taken, read from the end: one that is not among a block's own
    /// last `limit` cannot be among the last `limit` of all. Only those few have their time read.
    public static func rows(_ runs: [MightyGraphRun], limit: Int = visibleRows, definitions: [String: AgentDefinition] = [:],
                            advisor: [AdvisorNote] = []) -> [Row] {
        guard limit > 0 else { return [] }
        typealias Candidate = (stamp: String, make: (Date) -> Row)
        var candidates: [Candidate] = []
        let span = activity(runs)
        for index in span {
            let run = runs[index]
            let leadNode = MightyGraphLayout.nodeID(run, suffix: MightyGraphLayout.workSuffix)
            // A notice turn is the same session answering: named as the request is when it has no answer of its own yet.
            let leadName = leadName(run, else: runs[span.lowerBound])
            func lead(_ id: String, _ stamp: String, _ what: String, node: String = leadNode) -> Candidate {
                (stamp, { Row(id: id, time: $0, actor: .lead, who: leadName, ink: FlowRoles.ink(.lead), what: what, nodeID: node) })
            }
            if run.noticeRun == true {
                // A notice came in: at the first thing its turn recorded, else when the work it reports on ended.
                let arrived = run.rootEntries.first?.timestamp ?? run.agents.compactMap { $0.figures?.endedAt }.max()
                if let arrived, let line = line(L("graph.log.notice", ["title": BackgroundNoticeRuns.heading(run)])) {
                    candidates.append(lead(run.id + ":notice", arrived, line, node: MightyGraphLayout.nodeID(run, suffix: "request")))
                }
            }
            // Read from the end and put back in the order they were recorded.
            var latest: [Candidate] = []
            for entry in run.rootEntries.reversed() where latest.count < limit {
                guard let what = leadWords(entry) else { continue }
                latest.append(lead(run.id + ":" + entry.id, entry.timestamp, what))
            }
            candidates += latest.reversed()
            // The turn's own end: at its result, which stands at its last record. A notice taken into a running turn has
            // no reply of its own and so no end.
            if MightyGraphSupport.terminal(run.status), !run.quietNotice, let ended = run.resultEntries.first?.timestamp ?? run.rootEntries.last?.timestamp {
                candidates.append(lead(run.id + ":end", ended, FlowCard.stateWord(MobileMightySupport.status(run.status))))
            }
            for agent in run.flowAgents where agent.isSubagent && agent.sourceBlockId == nil {
                let node = MightyGraphLayout.nodeID(run, suffix: "agent:" + agent.id)
                let name = MightyGraphSupport.blockTitle(agent)
                let ink = FlowRoles.ink(.subagent, color: agent.identity?.type.flatMap { definitions[$0]?.color })
                func note(_ id: String, _ stamp: String, _ what: String) -> Candidate {
                    (stamp, { Row(id: id, time: $0, actor: .subagent(agent.id), who: name, ink: ink, what: what, nodeID: node) })
                }
                if let started = agent.figures?.startedAt {
                    let identity = FlowCard.identityLine(agent, definition: agent.identity?.type.flatMap { definitions[$0] })
                    candidates.append(note(node + ":start", started, ([L("graph.log.started")] + [identity].compactMap { $0 }).joined(separator: " · ")))
                }
                var calls: [Candidate] = []
                for entry in agent.entries.reversed() where calls.count < limit {
                    guard let activity = entry.activity, activity.kind != "turn",
                          let what = line(activity.summary.isEmpty ? activity.toolName ?? "" : activity.summary) else { continue }
                    calls.append(note(node + ":" + entry.id, entry.timestamp, what))
                }
                candidates += calls.reversed()
                if MightyGraphSupport.terminal(agent.status), let ended = agent.figures?.endedAt {
                    candidates.append(note(node + ":end", ended, FlowCard.stateWord(MobileMightySupport.status(agent.status))))
                }
            }
        }
        for item in advisor {
            candidates.append((item.time, { Row(id: item.id, time: $0, actor: .advisor, who: item.who, ink: FlowRoles.ink(.advisor), what: item.what, nodeID: item.nodeID) }))
        }
        // Each in the order it was read, so two things of one second stay as they were recorded.
        var timed: [(time: Date, order: Int, make: (Date) -> Row)] = []
        for (order, candidate) in candidates.enumerated() {
            guard let time = AgentRunTiming.parseTimestamp(candidate.stamp) else { continue }
            timed.append((time, order, candidate.make))
        }
        timed.sort { $0.time != $1.time ? $0.time < $1.time : $0.order < $1.order }
        return timed.suffix(limit).map { $0.make($0.time) }
    }

    /// The diagram block a row's click shows: its own, else the nearest block above it that the diagram drew (a block
    /// under a folded card is laid out nowhere), else its request's main session. Nil when the diagram drew none of them.
    public static func target(_ row: Row, in runs: [MightyGraphRun], drawn: (String) -> Bool) -> String? {
        if drawn(row.nodeID) { return row.nodeID }
        for index in activity(runs).reversed() {
            if let node = drawnNode(row.nodeID, in: runs[index], drawn: drawn) { return node }
            if runs[index].flowAgents.contains(where: { MightyGraphLayout.nodeID(runs[index], suffix: "agent:" + $0.id) == row.nodeID }) { return nil }
        }
        return nil
    }

    /// The block of `run` that stands for `nodeID` on the diagram: itself when drawn, else the nearest block above it
    /// that is, else the run's main session. Nil when `nodeID` is no sub-agent block of the run, or none of them is drawn.
    public static func drawnNode(_ nodeID: String, in run: MightyGraphRun, drawn: (String) -> Bool) -> String? {
        if drawn(nodeID) { return nodeID }
        let agents = run.flowAgents
        guard let own = agents.first(where: { MightyGraphLayout.nodeID(run, suffix: "agent:" + $0.id) == nodeID }) else { return nil }
        var above = own.parentID, depth = 0
        while let id = above, depth < 64, let parent = agents.first(where: { $0.id == id }) {
            let node = MightyGraphLayout.nodeID(run, suffix: "agent:" + parent.id)
            if drawn(node) { return node }
            above = parent.parentID
            depth += 1
        }
        let work = MightyGraphLayout.nodeID(run, suffix: MightyGraphLayout.workSuffix)
        return drawn(work) ? work : nil
    }

    /// What a record of the main session's says in the log: a tool call as the transcript words it, a question it
    /// asked the user, a context compaction. Its replies and the run's own lifecycle rows are not events of the log,
    /// and a call that starts a sub-agent is that sub-agent's own first row.
    static func leadWords(_ entry: LogEntry) -> String? {
        if let activity = entry.activity {
            guard activity.kind != "turn" else { return nil }
            let tool = activity.toolName ?? ""
            guard !["agent", "task"].contains(tool.lowercased()) else { return nil }
            let words = activity.summary.isEmpty ? tool : activity.summary
            return line(tool == "AskUserQuestion" ? L("graph.log.question", ["text": words]) : words)
        }
        guard entry.kind == "system", entry.text.hasPrefix(ContextCompaction.title) else { return nil }
        return line(entry.text)
    }

    static func line(_ raw: String) -> String? {
        let clean = ActivitySupport.clean(raw, maximumBytes: lineBytes, singleLine: true)
        return clean.isEmpty ? nil : clean
    }

    /// The main session in words: the model that answered last, else the model the request was set to, else the
    /// agent's name. `request` is the request a notice run answers for: the same session, whose model names it while the
    /// notice turn has no answer of its own.
    public static func leadName(_ run: MightyGraphRun, else request: MightyGraphRun? = nil) -> String {
        for candidate in [run, request].compactMap({ $0 }) {
            if let model = candidate.responseRecords?.last(where: { !($0.model ?? "").isEmpty })?.model { return ModelUsageFormat.shortName(model, versioned: true) }
        }
        if let label = ModelUsageFormat.versionedNodeLabel(run.nodeModelLabel ?? request?.nodeModelLabel) { return label }
        return ProviderOptions.label(run.provider ?? "claude")
    }

    /// A row's time as the strip writes it: hours, minutes and seconds in the reader's own time zone.
    public static func clock(_ date: Date, timeZone: TimeZone = .current) -> String {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = timeZone
        let parts = calendar.dateComponents([.hour, .minute, .second], from: date)
        return String(format: "%02d:%02d:%02d", parts.hour ?? 0, parts.minute ?? 0, parts.second ?? 0)
    }

    // MARK: Status

    /// What the advisor is doing, for the status line (`FlowAdvisor.status`): on call while the activity goes on, at
    /// work on a consultation, or how often it was consulted once the activity ended. Nil when the session has none.
    public enum AdvisorState: Equatable, Sendable { case onCall, consulting, consulted(Int) }

    /// The strip's header: who is doing what right now, in one line.
    public struct Status: Equatable, Sendable {
        /// "Haiku 5.5 실행 중": the main session and its state.
        public var lead: String
        /// "하위 에이전트 2/3 실행 중", or how many ended once nothing of the request's row runs; nil when the request
        /// delegated to none.
        public var subagents: String?
        /// "작업 2개 실행 중" while background commands of the request run; nil otherwise.
        public var tasks: String?
        /// "조언자 대기", "조언자 검토 중" or "조언자 호출 2"; nil without an advisor.
        public var advisor: String?
        public var text: String { parts.map(\.text).joined(separator: " · ") }
        /// The line's parts in order, each with the role whose ink it takes.
        public var parts: [Part] {
            [Part(role: .lead, text: lead)] + [subagents.map { Part(role: .subagent, text: $0) }, tasks.map { Part(role: .task, text: $0) },
                                               advisor.map { Part(role: .advisor, text: $0) }].compactMap { $0 }
        }
        public struct Part: Equatable, Sendable {
            public var role: FlowRole
            public var text: String
        }
    }

    /// The status of the pane's newest activity, nil for a pane without a request. The main session's state is the
    /// state of the run the views follow (`MightyGraphSupport.followRunIndex`): the request while it runs or awaits its
    /// notices, a notice turn while Claude answers one. The sub-agents are the newest request's.
    public static func status(_ runs: [MightyGraphRun], advisor: AdvisorState? = nil) -> Status? {
        guard let newest = MightyGraphSupport.newestRequestIndex(runs) else { return nil }
        let request = runs[newest]
        let followed = runs[MightyGraphSupport.followRunIndex(runs) ?? newest]
        let state: String
        if !MightyGraphSupport.terminal(followed.status) { state = FlowCard.stateWord(MobileMightySupport.status(followed.status)) }
        else if BackgroundNoticeRuns.awaitingNotice(request, in: runs) != nil { state = BackgroundNoticeRuns.stateWord }
        else { state = FlowCard.stateWord(MobileMightySupport.status(request.status)) }
        var subagents: String?, tasks: String?
        if let back = FlowReturn.state(request) {
            if back.subagentsRunning > 0 {
                subagents = L("graph.log.subagentsRunning", ["running": "\(back.subagentsRunning)", "total": "\(back.subagents)"])
            } else if back.subagents > 0, back.tasksRunning == 0 {
                // Ended sub-agents are worth a word once nothing else of the row runs.
                subagents = L("graph.log.subagentsDone", ["total": "\(back.subagents)"])
            }
            if back.tasksRunning > 0 { tasks = L("graph.log.tasksRunning", ["count": "\(back.tasksRunning)"]) }
        }
        let advising: String? = switch advisor {
        case .onCall: L("graph.log.advisorOnCall")
        case .consulting: L("graph.log.advisorConsulting")
        case .consulted(let count): L("graph.log.advisorCalls", ["count": "\(count)"])
        case nil: nil
        }
        return Status(lead: leadName(followed, else: request) + " " + state, subagents: subagents, tasks: tasks, advisor: advising)
    }
}
