import Foundation
import Testing
@testable import MightyCore

/// The flow's session log (`FlowLog`): the last events of the pane's newest activity with their time, who and what,
/// and the status line over them. It reads the runs the timeline draws and their blocks' own records.
struct FlowLogTests {
    private func stamp(_ seconds: Int) -> String { ISO8601DateFormatter().string(from: Date(timeIntervalSince1970: 1_800_000_000 + Double(seconds))) }
    private func date(_ seconds: Int) -> Date { Date(timeIntervalSince1970: 1_800_000_000 + Double(seconds)) }
    private func tool(_ id: String, _ summary: String, at seconds: Int, name: String = "Read", kind: String = "tool", state: String = "completed") -> LogEntry {
        LogEntry(id: id, kind: "system", text: summary, timestamp: stamp(seconds), provider: "claude",
                 activity: AgentActivity(id: id, provider: "claude", kind: kind, state: state, toolName: name, summary: summary))
    }
    private func said(_ id: String, _ text: String, at seconds: Int, kind: String = "assistant") -> LogEntry {
        LogEntry(id: id, kind: kind, text: text, timestamp: stamp(seconds), provider: "claude")
    }
    private func agent(_ id: String, parent: String? = nil, status: String = "running", background: Bool = false, kind: String? = nil, source: String? = nil,
                       entries: [LogEntry] = [], started: Int? = nil, ended: Int? = nil, type: String? = nil, resolved: String? = nil) -> MightyGraphAgent {
        let identity = GraphAgentIdentity(type: type, resolvedModel: resolved)
        let figures = GraphAgentFigures(startedAt: started.map(stamp), endedAt: ended.map(stamp))
        return MightyGraphAgent(id: id, parentID: parent, title: id, input: "확인", status: status, entries: entries, kind: kind,
                                background: background ? true : nil, sourceBlockId: source, identity: identity.isEmpty ? nil : identity,
                                figures: figures.isEmpty ? nil : figures)
    }
    private func run(_ id: String, status: String = "running", root: [LogEntry] = [], agents: [MightyGraphAgent] = [], notice: Bool = false, title: String? = nil,
                     model: String? = "claude-sonnet-5-5", output: String? = nil) -> MightyGraphRun {
        var run = MightyGraphRun(id: id, input: "요청 " + id, status: status, rootEntries: root, agents: agents, finalOutput: output,
                                 responseRecords: model.map { [GraphResponseRecord(responseId: id + "-m", model: $0, usage: GraphTokenUsage(inputTokens: 1, outputTokens: 1))] },
                                 provider: "claude", noticeRun: notice ? true : nil, noticeTitle: title)
        run.refreshResult()
        return run
    }
    private func words(_ rows: [FlowLog.Row]) -> [String] { rows.map { $0.who + " | " + $0.what } }

    // MARK: Rows

    @Test func aRowIsAToolCallAStartAnEndAQuestionOrACompactionWithItsTimeAndWho() {
        LocaleOverride.$language.withValue(.ko) {
            let root = [said("t1", "Looking around.", at: 1), tool("r1", "Sources/A.swift 읽기", at: 2), tool("spawn", "Agent: scout", at: 3, name: "Agent"),
                        tool("turn", "응답 생성", at: 4, name: "", kind: "turn"), tool("ask", "어느 쪽으로 할까요?", at: 9, name: "AskUserQuestion", state: "waiting"),
                        said("c1", ContextCompaction.title + " · 자동 정리 · 84,120 → 21,300 토큰", at: 10, kind: "system"), said("e1", "오류 줄", at: 11, kind: "error")]
            let scout = agent("scout", status: "completed", entries: [tool("s1", "grep tracker", at: 5, name: "Grep"), said("s2", "Found three.", at: 6), tool("s3", "B.swift 읽기", at: 7)],
                              started: 3, ended: 8, type: "Explore", resolved: "claude-haiku-5-5")
            let request = run("r", root: root, agents: [scout])
            let rows = FlowLog.rows([request], limit: 20)
            #expect(words(rows) == ["Sonnet 5.5 | Sources/A.swift 읽기", "scout | 시작 · Explore · Haiku 5.5", "scout | grep tracker", "scout | B.swift 읽기", "scout | 완료",
                                    "Sonnet 5.5 | 질문 · 어느 쪽으로 할까요?", "Sonnet 5.5 | 컨텍스트 정리 · 자동 정리 · 84,120 → 21,300 토큰"])
            #expect(rows.map(\.time) == [2, 3, 5, 7, 8, 9, 10].map(date))
            // Who each row is about, its ink and the block it leads to.
            #expect(rows[0].actor == .lead && rows[0].ink == FlowRoles.ink(.lead))
            #expect(rows[0].nodeID == MightyGraphLayout.nodeID(request, suffix: MightyGraphLayout.workSuffix))
            #expect(rows[1].actor == .subagent("scout") && rows[1].ink == .agent)
            #expect(rows[1].nodeID == MightyGraphLayout.nodeID(request, suffix: "agent:scout"))
            #expect(rows[1].actor.role == .subagent && rows[0].actor.role == .lead && FlowLog.Actor.advisor.role == .advisor)
            // Every row has an id of its own.
            #expect(Set(rows.map(\.id)).count == rows.count)
        }
    }

    @Test func theMainSessionsReplyItsLifecycleRowAndItsAgentCallAreNoRows() {
        #expect(FlowLog.leadWords(said("t", "Just words.", at: 0)) == nil)
        #expect(FlowLog.leadWords(tool("turn", "응답 생성", at: 0, name: "", kind: "turn")) == nil)
        #expect(FlowLog.leadWords(tool("a", "Agent: scout", at: 0, name: "Agent")) == nil)
        #expect(FlowLog.leadWords(tool("a", "Task: scout", at: 0, name: "Task")) == nil)
        #expect(FlowLog.leadWords(said("e", "오류", at: 0, kind: "error")) == nil)
        #expect(FlowLog.leadWords(said("s", "다른 시스템 줄", at: 0, kind: "system")) == nil)
        // A call without a summary is its tool's name; a long one is one bounded line.
        #expect(FlowLog.leadWords(tool("w", "", at: 0, name: "WebFetch")) == "WebFetch")
        let long = FlowLog.leadWords(tool("l", String(repeating: "가", count: 400) + "\nnext", at: 0))
        #expect((long?.utf8.count ?? 0) <= FlowLog.lineBytes && long?.contains("\n") == false)
    }

    @Test func onlyTheLastFewAreKeptOldestFirst() {
        let root = (0..<40).map { tool("r\($0)", "read \($0)", at: $0 * 2) }
        let busy = agent("busy", entries: (0..<40).map { tool("b\($0)", "grep \($0)", at: $0 * 2 + 1) }, started: 0)
        let request = run("r", root: root, agents: [busy])
        let rows = FlowLog.rows([request])
        #expect(rows.count == FlowLog.visibleRows)
        #expect(rows.map(\.what) == ["grep 37", "read 38", "grep 38", "read 39", "grep 39"])
        #expect(rows.map(\.time) == rows.map(\.time).sorted())
        #expect(FlowLog.rows([request], limit: 2).map(\.what) == ["read 39", "grep 39"])
        #expect(FlowLog.rows([request], limit: 0).isEmpty)
        #expect(FlowLog.rows([]).isEmpty)
    }

    @Test func twoThingsOfOneSecondStayInTheOrderTheyWereRecorded() {
        let request = run("r", root: [tool("a", "first", at: 5), tool("b", "second", at: 5), tool("c", "third", at: 5)])
        #expect(FlowLog.rows([request]).map(\.what) == ["first", "second", "third"])
        // A record whose time cannot be read is no row.
        var broken = request
        broken.rootEntries[1].timestamp = "not a time"
        #expect(FlowLog.rows([broken]).map(\.what) == ["first", "third"])
    }

    @Test func theLogIsOfTheNewestRequestAndTheNoticeRunsAfterIt() {
        LocaleOverride.$language.withValue(.ko) {
            let old = run("old", status: "completed", root: [tool("o1", "old read", at: 1), said("o2", "old answer", at: 2)], output: "old answer")
            let bg = agent("bg", status: "completed", background: true, entries: [tool("g1", "c.txt 읽기", at: 13)], started: 12, ended: 15)
            let request = run("new", status: "completed", root: [tool("n1", "new read", at: 11), said("n2", "launched", at: 14)], agents: [bg], output: "launched")
            let copy = agent("new:notice:1:bg", status: "completed", background: true, source: "bg", started: 12, ended: 15)
            let notice = run("new:notice:1", status: "completed", root: [said("x1", "done", at: 16)], agents: [copy], notice: true,
                             title: "백그라운드 작업 완료 · bg", output: "done")
            let runs = MightyGraphSupport.shownRuns([old, request, notice])
            #expect(FlowLog.activity(runs) == 1..<3)
            let rows = FlowLog.rows(runs, limit: 20)
            // Nothing of the older request; the background agent's own events, though no other view draws its block there;
            // the request's end at its last record; the notice at the first thing its turn recorded and the end of the
            // turn that answered it. A copy adds no row.
            #expect(words(rows) == ["Sonnet 5.5 | new read", "bg | 시작", "bg | c.txt 읽기", "Sonnet 5.5 | 완료", "bg | 완료", "Sonnet 5.5 | 알림 받음 · 백그라운드 작업 완료 · bg",
                                    "Sonnet 5.5 | 완료"])
            #expect(rows.map(\.time) == [11, 12, 13, 14, 15, 16, 16].map(date))
            #expect(rows[5].nodeID == MightyGraphLayout.nodeID(notice, suffix: "request"))
            #expect(rows[6].nodeID == MightyGraphLayout.nodeID(notice, suffix: MightyGraphLayout.workSuffix))
            // A notice whose turn has recorded nothing yet came when the work it reports on ended; while it answers, and
            // when it was taken into a running turn without a reply of its own, it has no end.
            var silent = notice; silent.rootEntries = []; silent.resultEntries = []; silent.status = "running"; silent.finalOutput = nil
            let answering = FlowLog.rows(MightyGraphSupport.shownRuns([old, request, silent]), limit: 20)
            #expect(answering.last?.what == "알림 받음 · 백그라운드 작업 완료 · bg" && answering.last?.time == date(15))
            var quiet = notice; quiet.finalOutput = nil; quiet.resultEntries = []
            #expect(FlowLog.rows(MightyGraphSupport.shownRuns([old, request, quiet]), limit: 20).last?.what == "알림 받음 · 백그라운드 작업 완료 · bg")
            // One that failed says so.
            var failed = notice; failed.status = "error"; failed.finalOutput = nil
            #expect(FlowLog.rows(MightyGraphSupport.shownRuns([old, request, failed]), limit: 20).last?.what == "실패")
            // A pane with only notice runs or only history still has its last run's log.
            #expect(FlowLog.activity([notice]) == 0..<1)
        }
    }

    @Test func aBlockWithoutFiguresHasNoStartOrEndRowAndACustomAgentsColourIsItsInk() {
        let plain = agent("plain", status: "completed", entries: [tool("p1", "read", at: 3)])
        let custom = agent("custom", status: "running", entries: [tool("c1", "grep", at: 4)], type: "reviewer")
        let nested = agent("nested", parent: "custom", status: "running", entries: [tool("d1", "deep", at: 5)])
        let task = agent("task", status: "running", background: true, kind: "task", entries: [said("t1", "launched", at: 6, kind: "system")])
        let rows = FlowLog.rows([run("r", agents: [plain, custom, nested, task])], limit: 20, definitions: ["reviewer": AgentDefinition(name: "reviewer", color: "green")])
        #expect(rows.map(\.what) == ["read", "grep", "deep"])
        #expect(rows.map(\.ink) == [.agent, .done, .agent])
        #expect(rows.map(\.who) == ["plain", "custom", "nested"])
    }

    @Test func theAdvisorsNotesAreRowsInTheAdvisorsInk() {
        let request = run("r", root: [tool("a", "read", at: 5)])
        let work = MightyGraphLayout.nodeID(request, suffix: MightyGraphLayout.workSuffix)
        let notes = [FlowLog.AdvisorNote(id: "adv-1", time: stamp(3), who: "Opus 5.5", what: "검토 중", nodeID: work),
                     FlowLog.AdvisorNote(id: "adv-2", time: stamp(7), who: "Opus 5.5", what: "조언 전달", nodeID: work),
                     FlowLog.AdvisorNote(id: "adv-3", time: "never", who: "Opus 5.5", what: "lost", nodeID: work)]
        let rows = FlowLog.rows([request], advisor: notes)
        #expect(words(rows) == ["Opus 5.5 | 검토 중", "Sonnet 5.5 | read", "Opus 5.5 | 조언 전달"])
        #expect(rows.map(\.actor) == [.advisor, .lead, .advisor])
        #expect(rows[0].ink == FlowRoles.ink(.advisor) && rows[0].nodeID == work)
    }

    @Test func theMainSessionIsNamedByTheModelThatAnswered() {
        LocaleOverride.$language.withValue(.ko) {
            #expect(FlowLog.leadName(run("r")) == "Sonnet 5.5")
            var configured = run("r", model: nil)
            configured.nodeModelLabel = "opus · 설정"
            #expect(FlowLog.leadName(configured) == "Opus · 설정")
            #expect(FlowLog.leadName(run("r", model: nil)) == "Claude")
            var codex = run("r", model: nil); codex.provider = "codex"
            #expect(FlowLog.leadName(codex) == "Codex")
        }
    }

    @Test func aRowLeadsToItsBlockOrTheNearestOneDrawnAboveIt() throws {
        let nested = agent("inner", parent: "outer", entries: [tool("i1", "grep", at: 4, name: "Grep")], started: 3)
        let outer = agent("outer", entries: [tool("o1", "ls", at: 2, name: "Bash")], started: 1)
        let request = run("r", root: [tool("m1", "A.swift 읽기", at: 0)], agents: [outer, nested])
        let rows = FlowLog.rows([request], limit: 20)
        let inner = try #require(rows.first { $0.id.hasSuffix(":i1") })
        let lead = try #require(rows.first { $0.actor == .lead })
        let work = MightyGraphLayout.nodeID(request, suffix: MightyGraphLayout.workSuffix)
        let outerNode = MightyGraphLayout.nodeID(request, suffix: "agent:outer"), innerNode = MightyGraphLayout.nodeID(request, suffix: "agent:inner")
        #expect(inner.nodeID == innerNode)
        // Its own block when the diagram drew it.
        #expect(FlowLog.target(inner, in: [request], drawn: { _ in true }) == innerNode)
        #expect(FlowLog.target(lead, in: [request], drawn: { _ in true }) == work)
        // Under a folded card the block is laid out nowhere: the card above it stands for it.
        #expect(FlowLog.target(inner, in: [request], drawn: { $0 != innerNode }) == outerNode)
        // Else the request's main session; nothing when the diagram drew none of them.
        #expect(FlowLog.target(inner, in: [request], drawn: { $0 == work }) == work)
        #expect(FlowLog.target(inner, in: [request], drawn: { _ in false }) == nil)
        #expect(FlowLog.target(lead, in: [request], drawn: { _ in false }) == nil)
        // A block that names itself as its parent ends the walk.
        let loop = run("r", agents: [agent("self", parent: "self", started: 1)])
        let own = try #require(FlowLog.rows([loop], limit: 20).first)
        #expect(FlowLog.target(own, in: [loop], drawn: { _ in false }) == nil)
        // The layout the pane draws holds every row's block.
        let layout = MightyGraphLayout.make(runs: [request], draft: "", running: true, expanded: [], splitsExecution: true, fanOut: .init(opened: []))
        let ids = Set(layout.nodes.map(\.id))
        #expect(FlowLog.target(inner, in: [request], drawn: ids.contains) == outerNode)
        #expect(FlowLog.target(lead, in: [request], drawn: ids.contains) == work)
        let opened = MightyGraphLayout.make(runs: [request], draft: "", running: true, expanded: [], splitsExecution: true, fanOut: .init(opened: [outerNode]))
        #expect(FlowLog.target(inner, in: [request], drawn: Set(opened.nodes.map(\.id)).contains) == innerNode)
    }

    @Test func theClockIsHoursMinutesAndSecondsInTheGivenZone() {
        let utc = TimeZone(identifier: "UTC")!, seoul = TimeZone(identifier: "Asia/Seoul")!
        let moment = Date(timeIntervalSince1970: 1_794_336_483)   // 2026-11-10T18:48:03Z
        #expect(FlowLog.clock(moment, timeZone: utc) == "18:48:03")
        #expect(FlowLog.clock(moment, timeZone: seoul) == "03:48:03")
    }

    // MARK: Status

    @Test func theStatusSaysWhoIsDoingWhatNow() throws {
        try LocaleOverride.$language.withValue(.ko) { () throws in
            let agents = [agent("a"), agent("b", status: "completed"), agent("c", background: true)]
            let running = try #require(FlowLog.status([run("r", agents: agents)]))
            #expect(running.lead == "Sonnet 5.5 실행 중")
            #expect(running.subagents == "하위 에이전트 2/3 실행 중")
            #expect(running.advisor == nil)
            #expect(running.text == "Sonnet 5.5 실행 중 · 하위 에이전트 2/3 실행 중")
            #expect(running.parts == [.init(role: .lead, text: "Sonnet 5.5 실행 중"), .init(role: .subagent, text: "하위 에이전트 2/3 실행 중")])
            #expect(FlowLog.status([run("r", agents: agents)], advisor: .onCall)?.parts.map(\.role) == [.lead, .subagent, .advisor])
            #expect(FlowLog.status([run("r")], advisor: .consulting)?.parts.map(\.role) == [.lead, .advisor])
            // With an advisor, waiting or at work.
            #expect(FlowLog.status([run("r", agents: agents)], advisor: .onCall)?.text == "Sonnet 5.5 실행 중 · 하위 에이전트 2/3 실행 중 · 조언자 대기")
            #expect(FlowLog.status([run("r")], advisor: .consulting)?.text == "Sonnet 5.5 실행 중 · 조언자 검토 중")
            // Every sub-agent ended.
            let ended = agents.map { agent -> MightyGraphAgent in var agent = agent; agent.status = "completed"; return agent }
            #expect(FlowLog.status([run("r", status: "completed", agents: Array(ended.prefix(2)), output: "done")])?.text == "Sonnet 5.5 완료 · 하위 에이전트 2개 끝남")
            #expect(FlowLog.status([run("r", status: "error")])?.text == "Sonnet 5.5 실패")
            #expect(FlowLog.status([]) == nil)
        }
    }

    @Test func aRequestAwaitingItsNoticesSaysSoAndANoticeTurnIsTheMainSessionAtWork() throws {
        try LocaleOverride.$language.withValue(.ko) { () throws in
            // The turn ended while its background agent runs.
            let bg = agent("bg", background: true)
            let awaiting = MightyGraphSupport.shownRuns([run("r", status: "completed", agents: [bg], output: "launched")])
            #expect(FlowLog.status(awaiting)?.text == "Sonnet 5.5 알림 대기 · 하위 에이전트 1/1 실행 중")
            // The agent ended and Claude answers its notice: the main session runs again.
            var done = bg; done.status = "completed"
            let copy = agent("r:notice:1:bg", status: "completed", background: true, source: "bg")
            let answering = [run("r", status: "completed", agents: [done], output: "launched"),
                             run("r:notice:1", status: "running", agents: [copy], notice: true, title: "백그라운드 작업 완료 · bg", model: "claude-haiku-5-5")]
            #expect(FlowLog.status(MightyGraphSupport.shownRuns(answering))?.text == "Haiku 5.5 실행 중 · 하위 에이전트 1개 끝남")
            // Answered: the request is done; the session is named by the model that answered last.
            var answered = answering
            answered[1].status = "completed"; answered[1].finalOutput = "done"
            #expect(FlowLog.status(MightyGraphSupport.shownRuns(answered))?.text == "Haiku 5.5 완료 · 하위 에이전트 1개 끝남")
            // A notice turn that has not answered yet is named as its request is.
            var unnamed = answering
            unnamed[1].responseRecords = nil
            #expect(FlowLog.status(MightyGraphSupport.shownRuns(unnamed))?.lead == "Sonnet 5.5 실행 중")
        }
    }

    // MARK: The recording

    @Test func theRecordedRequestLogsItsSubagentsWork() throws {
        try LocaleOverride.$language.withValue(.ko) { () throws in
            var session = RunSession(id: "pane", workspaceId: "space", title: "Claude", provider: "claude")
            session.recordGraph(RunEvent(sessionId: "pane", type: "log", entry: LogEntry(id: "request-one", kind: "user", text: "probe", provider: "claude")))
            let box = GraphAgentIdentityTests.Box(session)
            let parser = CLIStreamParser(provider: "claude", log: { _, _ in }, resume: { _ in }, activityNamespace: "recorded",
                                         graph: { box.value.recordGraph(RunEvent(sessionId: "pane", type: "graph", graph: $0)) }, graphInput: "probe")
            for line in try GraphAgentIdentityTests.fixture("claude-stream-subagents") { parser.push(line + Data([10])) }
            parser.flush(); parser.finishGraph(state: "completed")
            let runs = MightyGraphSupport.shownRuns(box.value.mightyGraphRuns)
            let rows = FlowLog.rows(runs, limit: 40)
            // Both sub-agents started, read and ended; the starts are at the times the CLI's own frames name.
            let starts = rows.filter { $0.id.hasSuffix(":start") }
            #expect(starts.map(\.who) == ["count a", "count b and c"])
            #expect(starts.map(\.what) == ["시작 · Explore · Haiku 5.5", "시작 · Explore · Haiku 5.5"])
            #expect(starts.map(\.time) == ["2026-11-10T18:56:31.912Z", "2026-11-10T18:56:44.496Z"].compactMap(AgentRunTiming.parseTimestamp))
            #expect(rows.filter { $0.id.hasSuffix(":end") && $0.actor != .lead }.map(\.who) == ["count a", "count b and c"])
            #expect(rows.filter { $0.actor == .subagent(runs[0].flowAgents.first { $0.title == "count b and c" }?.id ?? "") && !$0.id.hasSuffix(":start") && !$0.id.hasSuffix(":end") }.count == 2)
            // The background agent's end came in as a notice.
            #expect(rows.contains { $0.what == "알림 받음 · 백그라운드 작업 완료 · count b and c" })
            #expect(FlowLog.status(runs)?.text == "Haiku 5.5 완료 · 하위 에이전트 2개 끝남")
        }
    }

    // MARK: The pane's state

    @Test func theFoldedStripIsRememberedWithThePaneAndOlderSavesAreOpen() throws {
        let old = #"{"id":"p","workspaceId":"w","title":"Claude"}"#
        let pane = try JSONDecoder().decode(RunSession.self, from: Data(old.utf8))
        #expect(pane.flowLogCollapsed == nil)
        #expect(!String(decoding: try JSONEncoder().encode(pane), as: UTF8.self).contains("flowLogCollapsed"))
        var folded = pane
        folded.flowLogCollapsed = true
        let saved = try JSONEncoder().encode(folded)
        #expect(try JSONDecoder().decode(RunSession.self, from: saved).flowLogCollapsed == true)
        // Anything but true is open.
        let odd = #"{"id":"p","workspaceId":"w","title":"Claude","flowLogCollapsed":false}"#
        #expect(try JSONDecoder().decode(RunSession.self, from: Data(odd.utf8)).flowLogCollapsed == nil)
        let damaged = #"{"id":"p","workspaceId":"w","title":"Claude","flowLogCollapsed":"yes"}"#
        #expect(try JSONDecoder().decode(RunSession.self, from: Data(damaged.utf8)).flowLogCollapsed == nil)
    }

    @Test func aShortPaneKeepsTheDiagramAndShowsTheStripsStatusLineAlone() {
        // The Mac strip: a 23 pt header and 96 pt of rows; the diagram keeps 160 pt before the rows show.
        let head = 23.0, rows = 96.0
        #expect(FlowLog.rowsFit(room: head + rows + FlowLog.diagramRoom, head: head, rows: rows))
        #expect(FlowLog.rowsFit(room: 900, head: head, rows: rows))
        // A flow no taller than the open strip: before, the strip kept its rows and left the diagram 0 pt.
        #expect(!FlowLog.rowsFit(room: head + rows + FlowLog.diagramRoom - 1, head: head, rows: rows))
        #expect(!FlowLog.rowsFit(room: head + rows, head: head, rows: rows))
        // Not measured yet: the rows show.
        #expect(FlowLog.rowsFit(room: 0, head: head, rows: rows))
    }
}
