import Foundation

/// The role view as a phone draws it (docs/mobile-remote.md, "역할 보기"): who each block is, a running sub-agent's or a
/// background command's compact row, the delegate line, the way back, the advisor and the session log's status line.
/// Every word is the desktop's own, read with the very rules the Mac's flow is drawn by (`FlowRoles`, `FlowCard`,
/// `FlowDelegation`, `FlowReturn`, `FlowAdvisor`, `FlowLog`); the phone only lays it out and keeps a card's elapsed
/// time against its own clock. Inks travel as design token names (`FlowInk.tokenName`), times as milliseconds since
/// 1970. Sent only to a phone that asked for it (`roles=1`, capability `roles`). Windows Core builds the same shapes
/// (`MobileFlow.cs`), and both hosts' tests produce `native/contracts/fixtures/mobile-roles.json`.
public enum MobileFlow {
    /// What a payload with the role view is built with. `fanOut`: the pane draws the role view (a Claude pane): a
    /// request's background blocks travel in it as compact rows, with the delegate lines, the way back, the advisor and
    /// the log. Without it a block still says who it is, as on the Mac for every agent. `definitions` are the custom
    /// agents the blocks' types name (`AgentDefinitions`).
    public struct Roles: Sendable, Equatable {
        public var fanOut: Bool
        public var definitions: [String: AgentDefinition]
        public init(fanOut: Bool, definitions: [String: AgentDefinition] = [:]) { self.fanOut = fanOut; self.definitions = definitions }
    }

    /// A time as the wire carries it: milliseconds since 1970, rounded, so both hosts write the same number.
    static func milliseconds(_ date: Date?) -> Int? {
        date.map { Int(($0.timeIntervalSince1970 * 1_000).rounded()) }
    }

    /// The main session's identity on a run's `main` block: its model (the one that answered last, else the run's,
    /// else the agent's name, `FlowLog.leadName`) and the pane's effort when it named one. `request`: the request a
    /// notice run answers for.
    public static func lead(_ run: MightyGraphRun, request: MightyGraphRun?) -> MobileBlockRole {
        let who = FlowRoles.lead(run)
        let effort = who.effort.map { L("composer.effort.menu", ["effort": $0]) }
        let identity = ([FlowLog.leadName(run, else: request)] + [effort].compactMap { $0 }).joined(separator: " · ")
        return MobileBlockRole(name: who.role.rawValue, ink: who.ink.tokenName, identity: identity, marks: nil, summary: who.summary)
    }

    /// A child block's identity: a sub-agent's type and model with its marks, or, in the role view, a background
    /// command's `작업 · <command>`. Nil for a block that is neither.
    public static func role(_ agent: MightyGraphAgent, in run: MightyGraphRun, roles: Roles, output: [String] = []) -> MobileBlockRole? {
        let definition = agent.identity?.type.flatMap { roles.definitions[$0] }
        if let who = FlowRoles.subagent(agent, definition: definition) {
            return MobileBlockRole(name: who.role.rawValue, ink: who.ink.tokenName, identity: FlowCard.identityLine(who, agent),
                                   marks: who.marks.isEmpty ? nil : who.marks.map(\.rawValue), summary: who.summary)
        }
        guard roles.fanOut, let who = FlowRoles.task(agent) else { return nil }
        let content = FlowCard.content(agent, in: run, output: output)
        return MobileBlockRole(name: who.role.rawValue, ink: who.ink.tokenName, identity: content.identity,
                               marks: who.marks.isEmpty ? nil : who.marks.map(\.rawValue), summary: who.summary)
    }

    /// The compact row of a request's background sub-agent or command, or of a sub-agent still running
    /// (`FlowCard.foldsToCard`); nil for every other block. `window` are the runs the payload carries: a finished
    /// background block's row leads to its copy in a notice run among them.
    public static func card(_ agent: MightyGraphAgent, in run: MightyGraphRun, window: [MightyGraphRun], roles: Roles, output: [String] = []) -> MobileBlockCard? {
        guard roles.fanOut, FlowCard.foldsToCard(agent) else { return nil }
        let definition = agent.identity?.type.flatMap { roles.definitions[$0] }
        let content = FlowCard.content(agent, in: run, definition: definition, output: agent.isTask ? output : [])
        let (before, after) = FlowCard.figureParts(content)
        var copy: MobileCopyRef?
        if agent.background == true, MightyGraphLayout.terminal(agent.status) {
            for notice in window.reversed() where notice.noticeRun == true {
                if let copied = notice.agents.last(where: { $0.sourceBlockId == agent.id }) { copy = MobileCopyRef(runId: notice.id, blockId: copied.id); break }
            }
        }
        return MobileBlockCard(activity: content.activity, before: before, after: after, startedAt: milliseconds(content.startedAt),
                               endedAt: milliseconds(content.endedAt), nested: content.nested > 0 ? content.nested : nil, copy: copy)
    }

    /// The line over the rows block `key` (`FlowDelegation.mainKey`, or a sub-agent's id) delegated to; nil when it
    /// delegated to none, and outside the role view.
    public static func delegate(_ key: String, in run: MightyGraphRun, roles: Roles) -> MobileDelegateLine? {
        guard roles.fanOut, let line = FlowDelegation.line(FlowDelegation.children(of: key, in: run), definitions: roles.definitions) else { return nil }
        return MobileDelegateLine(head: line.head, parts: line.parts.isEmpty ? nil : line.parts.map { MobileInkedText(text: $0.text, ink: FlowRoles.ink($0.role).tokenName) },
                                  model: line.model, progress: line.progress, text: line.text)
    }

    /// The way back at the request's result position, while the result is not there (`FlowReturn`).
    public static func returning(_ run: MightyGraphRun, roles: Roles) -> MobileReturn? {
        guard roles.fanOut, !MightyGraphLayout.hasResult(run), let state = FlowReturn.state(run) else { return nil }
        return MobileReturn(text: state.text, waiting: state.waiting)
    }

    /// The advisor's band of `run` (`FlowAdvisor.band`): every consultation it keeps, oldest first.
    public static func advisor(_ run: MightyGraphRun, roles: Roles) -> MobileAdvisor? {
        guard roles.fanOut, let band = FlowAdvisor.band(run, definitions: roles.definitions) else { return nil }
        let rows = band.rows.map { row in
            MobileAdvisorRow(id: row.id, caller: row.caller, ink: row.ink.tokenName, at: milliseconds(row.started), seconds: row.seconds.map { Int($0) },
                             line: row.line, state: row.state)
        }
        return MobileAdvisor(header: band.header, state: band.state.map { $0 == .consulting ? "consulting" : "onCall" }, rows: rows, calls: band.calls,
                             totals: band.totals, note: band.note, empty: band.empty)
    }

    /// The session log strip of a pane's runs (`FlowLog`): its status line in parts and the newest activity's last rows,
    /// oldest first. Nil for a pane without a request.
    public static func log(_ source: [MightyGraphRun], definitions: [String: AgentDefinition] = [:]) -> MobileFlowLog? {
        let runs = MightyGraphSupport.shownRuns(source)
        guard let status = FlowLog.status(runs, advisor: FlowAdvisor.status(runs)) else { return nil }
        let rows = FlowLog.rows(runs, definitions: definitions, advisor: FlowAdvisor.notes(runs)).map { row in
            MobileFlowLogRow(id: row.id, at: milliseconds(row.time) ?? 0, who: row.who, ink: row.ink.tokenName, what: row.what)
        }
        return MobileFlowLog(status: status.parts.map { MobileInkedText(text: $0.text, ink: FlowRoles.ink($0.role).tokenName) }, rows: rows)
    }
}

/// A word in its ink: a part of the status line or of a delegate line.
public struct MobileInkedText: Codable, Sendable, Equatable {
    public var text: String
    /// A design token name (`FlowInk.tokenName`).
    public var ink: String
    public init(text: String, ink: String) { self.text = text; self.ink = ink }
}

/// Who a block is (`MobileBlock.role`).
public struct MobileBlockRole: Codable, Sendable, Equatable {
    /// `lead`, `subagent` or `task` (`FlowRole`).
    public var name: String
    /// The role's ink, or a custom agent's own colour, as a design token name.
    public var ink: String
    /// One line: the lead's model and effort ("Sonnet 5.5 · 사고 강도 High"), a sub-agent's type and model
    /// ("Explore · Haiku 5.5"), a background command's "작업 · <command>"; absent when none is known.
    public var identity: String?
    /// `background`, `fork`, `worktree`, in that order; absent when none.
    public var marks: [String]?
    /// The whole identity in one line, for a screen reader (`FlowIdentity.summary`).
    public var summary: String
    public init(name: String, ink: String, identity: String?, marks: [String]?, summary: String) {
        self.name = name; self.ink = ink; self.identity = identity; self.marks = marks; self.summary = summary
    }
}

/// A notice run's copy of a finished background block: where its compact row leads.
public struct MobileCopyRef: Codable, Sendable, Equatable {
    public var runId: String
    public var blockId: String
    public init(runId: String, blockId: String) { self.runId = runId; self.blockId = blockId }
}

/// The compact row of a sub-agent or a background command (`MobileBlock.card`, `FlowCard`).
public struct MobileBlockCard: Codable, Sendable, Equatable {
    /// What it is doing now while it runs, what it answered once it ended, a command's last output line.
    public var activity: String?
    /// The figures' words before and after its time ("실행 중"; "도구 3", "12.3K 토큰"): the phone puts the time between
    /// them, read against its own clock from `startedAt` while it runs, from `startedAt` to `endedAt` once it ended.
    public var before: [String]
    public var after: [String]
    /// Milliseconds since 1970, as the app saw it start and end; absent when it did not see them.
    public var startedAt: Int?
    public var endedAt: Int?
    /// Sub-agents under it, which the desktop's card counts in their place; absent when none.
    public var nested: Int?
    /// A finished background block's full copy in a notice run the payload carries; absent otherwise.
    public var copy: MobileCopyRef?
    public init(activity: String?, before: [String], after: [String], startedAt: Int?, endedAt: Int?, nested: Int?, copy: MobileCopyRef?) {
        self.activity = activity; self.before = before; self.after = after; self.startedAt = startedAt; self.endedAt = endedAt
        self.nested = nested; self.copy = copy
    }
}

/// The line over the rows a block delegated to (`MobileBlock.delegate`, `FlowDelegation.Line`).
public struct MobileDelegateLine: Codable, Sendable, Equatable {
    /// "하위 에이전트 3개에 위임", "작업 2개 실행", "3개 병렬".
    public var head: String
    /// A row of both: how many of each, in that role's ink; absent otherwise.
    public var parts: [MobileInkedText]?
    /// The model the sub-agents share, or how many they are on (drawn in the sub-agent ink).
    public var model: String?
    /// "2/3 실행 중" while any of them runs (drawn in the running blue).
    public var progress: String?
    /// The whole line.
    public var text: String
    public init(head: String, parts: [MobileInkedText]?, model: String?, progress: String?, text: String) {
        self.head = head; self.parts = parts; self.model = model; self.progress = progress; self.text = text
    }
}

/// The way back to the main session where a request's result will come (`MobileMightyRun.returning`, `FlowReturn`).
public struct MobileReturn: Codable, Sendable, Equatable {
    public var text: String
    /// Something it handed work to still runs.
    public var waiting: Bool
    public init(text: String, waiting: Bool) { self.text = text; self.waiting = waiting }
}

/// The advisor of a run (`MobileMightyRun.advisor`, `FlowAdvisor.Band`).
public struct MobileAdvisor: Codable, Sendable, Equatable {
    /// "조언자 · Opus 5.5 · 검토 중".
    public var header: String
    /// `consulting` or `onCall` while the run goes on; absent once it and everything it started ended.
    public var state: String?
    /// Every consultation the run keeps, oldest first.
    public var rows: [MobileAdvisorRow]
    /// Every consultation seen (more than `rows` once older ones were let go).
    public var calls: Int
    /// "호출 1 · 읽은 토큰 34K".
    public var totals: String
    /// The reply went to Claude encrypted, and nothing of it is shown.
    public var note: String?
    /// What it says in its rows' place before any consultation.
    public var empty: String?
    public init(header: String, state: String?, rows: [MobileAdvisorRow], calls: Int, totals: String, note: String?, empty: String?) {
        self.header = header; self.state = state; self.rows = rows; self.calls = calls; self.totals = totals; self.note = note; self.empty = empty
    }
}

/// One consultation of the advisor.
public struct MobileAdvisorRow: Codable, Sendable, Equatable {
    public var id: String
    /// The main session's word or the sub-agent's name, in that role's ink.
    public var caller: String
    public var ink: String
    /// Milliseconds since 1970 when it was asked; how many whole seconds the reply took once it came.
    public var at: Int?
    public var seconds: Int?
    /// "답 직전 마지막 단계 · 조언 전달 · 내용 비표시": where in the caller's work, and how it ended.
    public var line: String
    /// `consulting`, `done`, `declined`, `error` or `stopped`.
    public var state: String
    public init(id: String, caller: String, ink: String, at: Int?, seconds: Int?, line: String, state: String) {
        self.id = id; self.caller = caller; self.ink = ink; self.at = at; self.seconds = seconds; self.line = line; self.state = state
    }
}

/// The session log strip (`MobileMighty.log`, `FlowLog`).
public struct MobileFlowLog: Codable, Sendable, Equatable {
    /// "Sonnet 5.5 실행 중 · 작업 2개 실행 중 · 조언자 대기", each part in its role's ink.
    public var status: [MobileInkedText]
    /// The newest activity's last rows (at most `FlowLog.visibleRows`), oldest first.
    public var rows: [MobileFlowLogRow]
    public init(status: [MobileInkedText], rows: [MobileFlowLogRow]) { self.status = status; self.rows = rows }
}

/// One row of the session log: when (milliseconds since 1970), who, in whose ink, and what.
public struct MobileFlowLogRow: Codable, Sendable, Equatable {
    public var id: String
    public var at: Int
    public var who: String
    public var ink: String
    public var what: String
    public init(id: String, at: Int, who: String, ink: String, what: String) { self.id = id; self.at = at; self.who = who; self.ink = ink; self.what = what }
}
