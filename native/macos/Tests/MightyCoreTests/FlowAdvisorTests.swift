import CoreGraphics
import Foundation
import Testing
@testable import MightyCore

/// The advisor's band beside a run in the flow (`FlowAdvisor`): what it says of the advisor a Claude session asks, where
/// the layout puts it and its dotted links, and what it gives the session log. Everything it says is read from the
/// run's own record of what the stream showed (`GraphAdvisor`).
struct FlowAdvisorTests {
    private func stamp(_ seconds: Int) -> String { ISO8601DateFormatter().string(from: Date(timeIntervalSince1970: 1_800_000_000 + Double(seconds))) }
    private func date(_ seconds: Int) -> Date { Date(timeIntervalSince1970: 1_800_000_000 + Double(seconds)) }
    private func agent(_ id: String, parent: String? = nil, status: String = "running", background: Bool = false, type: String? = nil) -> MightyGraphAgent {
        MightyGraphAgent(id: id, parentID: parent, title: id, input: "확인", status: status, background: background ? true : nil,
                         identity: type.map { GraphAgentIdentity(type: $0) })
    }
    private func call(_ id: String, caller: String? = nil, at start: Int? = nil, until end: Int? = nil, state: String = "done", text: String? = nil, code: String? = nil,
                      before: Bool = false, followed: Bool = false) -> GraphAdvisorCall {
        GraphAdvisorCall(id: id, caller: caller, startedAt: start.map(stamp), endedAt: end.map(stamp), state: state, redacted: state == "done" && text == nil ? true : nil,
                         text: text, errorCode: code, beforeChange: before ? true : nil, followed: followed ? true : nil)
    }
    private func run(_ id: String, status: String = "running", agents: [MightyGraphAgent] = [], calls: [GraphAdvisorCall]? = nil, model: String? = nil,
                     usage: GraphTokenUsage? = nil, configured: String? = nil, total: Int? = nil, notice: Bool = false, output: String? = nil, provider: String = "claude") -> MightyGraphRun {
        var run = MightyGraphRun(id: id, input: "요청 " + id, status: status, agents: agents, finalOutput: output,
                                 responseRecords: [GraphResponseRecord(responseId: id + "-m", model: "claude-sonnet-5-5", usage: GraphTokenUsage(inputTokens: 1, outputTokens: 1))],
                                 provider: provider, noticeRun: notice ? true : nil,
                                 advisor: calls == nil && model == nil && usage == nil ? nil : GraphAdvisor(model: model, usage: usage, calls: calls ?? [], total: total),
                                 configuredAdvisor: configured)
        run.refreshResult()
        return run
    }
    private func layout(_ runs: [MightyGraphRun], opened: Set<String> = [], fanOut: Bool = true) -> MightyGraphLayout {
        MightyGraphLayout.make(runs: runs, draft: "", running: true, expanded: [], splitsExecution: true, fanOut: fanOut ? .init(opened: opened) : nil)
    }
    private func node(_ layout: MightyGraphLayout, _ run: MightyGraphRun, _ suffix: String) -> MightyGraphLayout.Node? {
        layout.nodes.first { $0.id == MightyGraphLayout.nodeID(run, suffix: suffix) }
    }

    // MARK: Whether there is a band

    @Test func aRunHasABandWhenAnAdvisorWasOnOrWasSeenAtWork() {
        LocaleOverride.$language.withValue(.ko) {
            #expect(FlowAdvisor.band(run("plain")) == nil)
            #expect(!FlowAdvisor.shows(run("plain")))
            // Known to be on when the request started, never asked yet: a band with no row, waiting.
            let waiting = FlowAdvisor.band(run("on", configured: "opus"))
            #expect(waiting?.header == "조언자 · Opus · 요청 · 대기 중")
            #expect(waiting?.rows.isEmpty == true && waiting?.calls == 0 && waiting?.totals == "호출 0" && waiting?.note == nil)
            #expect(waiting?.empty == "아직 조언을 구하지 않았습니다")
            // The run ended without asking: the band says so and waits for nothing.
            let unused = FlowAdvisor.band(run("ended", status: "completed", configured: "opus", output: "done"))
            #expect(unused?.header == "조언자 · Opus · 요청" && unused?.empty == "조언을 구하지 않았습니다")
            // Not known to be on, but the stream showed it at work.
            let used = FlowAdvisor.band(run("used", calls: [call("srvtoolu_a", at: 1, state: "consulting")]))
            #expect(used?.header == "조언자 · 검토 중" && used?.empty == nil)
            // Only what the turn's result said it cost (a transcript read from its middle).
            #expect(FlowAdvisor.band(run("cost", status: "completed", model: "claude-opus-5-5", usage: GraphTokenUsage(inputTokens: 900)))?.header == "조언자 · Opus 5.5")
            // A notice run inherits what its request was started with: it has a band once its own turn asked, with the
            // model its request named until a result names one.
            #expect(FlowAdvisor.band(run("r:notice:1", configured: "opus", notice: true)) == nil)
            #expect(FlowAdvisor.band(run("r:notice:1", calls: [call("srvtoolu_n", at: 1, until: 2)], configured: "opus", notice: true))?.header == "조언자 · Opus · 요청 · 대기 중")
            // Another agent has no advisor, whatever its run carries.
            #expect(FlowAdvisor.band(run("codex", configured: "opus", provider: "codex")) == nil)
        }
    }

    // MARK: The recording

    @Test func theRecordedConsultationReadsAsItHappened() throws {
        try LocaleOverride.$language.withValue(.ko) { () throws in
            var session = RunSession(id: "pane", workspaceId: "space", title: "Claude", provider: "claude")
            session.recordGraph(RunEvent(sessionId: "pane", type: "log", entry: LogEntry(id: "request-one", kind: "user", text: "probe", provider: "claude")))
            let box = GraphAgentIdentityTests.Box(session)
            let parser = CLIStreamParser(provider: "claude", log: { _, _ in }, resume: { _ in }, activityNamespace: "recorded",
                                         graph: { box.value.recordGraph(RunEvent(sessionId: "pane", type: "graph", graph: $0)) }, graphInput: "probe")
            for line in try GraphAgentIdentityTests.fixture("claude-stream-advisor") { parser.push(line + Data([10])) }
            parser.flush(); parser.finishGraph(state: "completed")
            let runs = MightyGraphSupport.shownRuns(box.value.mightyGraphRuns)
            let band = try #require(FlowAdvisor.band(runs[0]))
            // The model the result named; the run ended, so the band says nothing of waiting.
            #expect(band.header == "조언자 · Opus 5.5")
            #expect(band.state == nil && band.calls == 1 && band.earlier == 0)
            let row = try #require(band.rows.first)
            #expect(row.role == .lead && row.caller == "메인" && row.ink == FlowRoles.ink(.lead))
            #expect(row.callerNodeID == MightyGraphLayout.nodeID(runs[0], suffix: MightyGraphLayout.workSuffix))
            // Nothing but the answer followed it, and its reply was encrypted.
            #expect(row.position == .lastStep)
            #expect(row.line == "답 직전 마지막 단계 · 조언 전달 · 내용 비표시")
            #expect(row.text == nil)
            #expect(row.started == AgentRunTiming.parseTimestamp("2026-11-10T18:16:51.658Z"))
            #expect(abs((row.seconds ?? 0) - 7.882) < 0.001)
            #expect(band.totals == "호출 1 · 읽은 토큰 34K")
            #expect(band.note == "조언은 암호화되어 Claude에게만 전달됩니다. 내용은 표시되지 않습니다.")
            // The diagram: a band left of the request's column with one dotted link to the main session's block.
            let graph = layout(runs)
            let bandNode = try #require(node(graph, runs[0], FlowAdvisor.suffix))
            let work = try #require(node(graph, runs[0], MightyGraphLayout.workSuffix))
            #expect(bandNode.frame.maxX + FlowAdvisor.gap == work.frame.minX)
            #expect(graph.advisorLinks == [MightyGraphLayout.AdvisorLink(band: bandNode.id, target: work.id)])
            // The log: the consultation's start and its end, in the advisor's ink, and the status once it is over.
            let rows = FlowLog.rows(runs, limit: 10, advisor: FlowAdvisor.notes(runs))
            #expect(rows.map { $0.who + " | " + $0.what } == ["조언자 · Opus 5.5 | 메인의 요청으로 검토 시작", "조언자 · Opus 5.5 | 조언 전달 · 내용 비표시", "Sonnet 5.5 | 완료"])
            #expect(rows.prefix(2).allSatisfy { $0.actor == .advisor && $0.ink == FlowRoles.ink(.advisor) && $0.nodeID == bandNode.id })
            #expect(FlowLog.status(runs, advisor: FlowAdvisor.status(runs))?.text == "Sonnet 5.5 완료 · 조언자 호출 1")
            #expect(FlowLog.target(rows[0], in: runs, drawn: Set(graph.nodes.map(\.id)).contains) == bandNode.id)
        }
    }

    // MARK: Rows

    @Test func aConsultationStandsBeforeAnyFileChangeInTheWorkOrAsTheLastStep() {
        let early = call("a", before: true, followed: true), middle = call("b", followed: true), last = call("c"), earlyLast = call("d", before: true)
        // While the caller works, only what was seen when it asked is said.
        #expect(FlowAdvisor.position(early, callerAnswered: false) == .beforeChange)
        #expect(FlowAdvisor.position(middle, callerAnswered: false) == .during)
        #expect(FlowAdvisor.position(last, callerAnswered: false) == .during)
        #expect(FlowAdvisor.position(earlyLast, callerAnswered: false) == .beforeChange)
        // Once the caller answered, the one nothing followed was its last step.
        #expect(FlowAdvisor.position(early, callerAnswered: true) == .beforeChange)
        #expect(FlowAdvisor.position(middle, callerAnswered: true) == .during)
        #expect(FlowAdvisor.position(last, callerAnswered: true) == .lastStep)
        #expect(FlowAdvisor.position(earlyLast, callerAnswered: true) == .lastStep)
        LocaleOverride.$language.withValue(.ko) {
            #expect([FlowAdvisor.Position.beforeChange, .during, .lastStep].map(FlowAdvisor.words) == ["파일 변경 전", "작업 중", "답 직전 마지막 단계"])
        }
        LocaleOverride.$language.withValue(.en) {
            #expect([FlowAdvisor.Position.beforeChange, .during, .lastStep].map(FlowAdvisor.words) == ["before any file change", "during the work", "last step before the answer"])
        }
    }

    @Test func aRowSaysWhoAskedWhenAndHowItEnded() throws {
        try LocaleOverride.$language.withValue(.ko) { () throws in
            let scout = agent("scout", status: "completed", type: "reviewer"), busy = agent("busy")
            let calls = [call("c1", at: 1, until: 9, before: true, followed: true),
                         call("c2", caller: "scout", at: 10, until: 12, text: "Check the cache key first."),
                         call("c3", caller: "busy", at: 13, state: "consulting"),
                         call("c4", at: 14, until: 15, state: "declined", followed: true),
                         call("c5", at: 16, until: 17, state: "error", code: "overloaded", followed: true),
                         call("c6", at: 18, until: 19, state: "error", followed: true),
                         call("c7", at: 20, until: 21, state: "stopped")]
            let request = run("r", agents: [scout, busy], calls: calls, model: "claude-opus-5-5", usage: GraphTokenUsage(inputTokens: 1_200, outputTokens: 80, cacheReadTokens: 300))
            let band = try #require(FlowAdvisor.band(request, definitions: ["reviewer": AgentDefinition(name: "reviewer", color: "green")]))
            #expect(band.header == "조언자 · Opus 5.5 · 검토 중")
            #expect(band.rows.map(\.caller) == ["메인", "scout", "busy", "메인", "메인", "메인", "메인"])
            #expect(band.rows.map(\.role) == [.lead, .subagent, .subagent, .lead, .lead, .lead, .lead])
            // A custom agent's own colour, as on its block.
            #expect(band.rows.map(\.ink) == [FlowRoles.ink(.lead), .done, .agent, FlowRoles.ink(.lead), FlowRoles.ink(.lead), FlowRoles.ink(.lead), FlowRoles.ink(.lead)])
            #expect(band.rows[1].callerNodeID == MightyGraphLayout.nodeID(request, suffix: "agent:scout"))
            #expect(band.rows.map(\.line) == ["파일 변경 전 · 조언 전달 · 내용 비표시", "답 직전 마지막 단계 · 조언 전달", "작업 중 · 검토 중", "작업 중 · 조언자가 답하지 않음",
                                              "작업 중 · 오류 · overloaded", "작업 중 · 오류", "작업 중 · 결과 전에 중단"])
            // The advice only where the CLI gave it; how long a reply took, not for one still out.
            #expect(band.rows.map(\.text) == [nil, "Check the cache key first.", nil, nil, nil, nil, nil])
            #expect(band.rows.map(\.seconds) == [8, 2, nil, 1, 1, 1, 1])
            #expect(band.rows.map(\.started) == [1, 10, 13, 14, 16, 18, 20].map(date))
            // The note stands for the one reply that had no text; what the advisor read is all of its input.
            #expect(band.hidesText && band.totals == "호출 7 · 읽은 토큰 1.5K")
            // A caller the run no longer holds is still a row, about the main session's block.
            let orphan = try #require(FlowAdvisor.band(run("o", calls: [call("x", caller: "gone", at: 1, until: 2)])))
            #expect(orphan.rows[0].caller == "메인" && orphan.rows[0].callerNodeID.hasSuffix(MightyGraphLayout.workSuffix))
            // Without a reply that hid its text there is no note.
            #expect(FlowAdvisor.band(run("t", calls: [call("y", text: "Do it.")]))?.note == nil)
        }
    }

    @Test func theBandSaysWaitingOnlyWhileTheRunOrItsWorkGoesOn() {
        let done = call("a", at: 1, until: 2)
        #expect(FlowAdvisor.band(run("r", calls: [done]))?.state == .onCall)
        #expect(FlowAdvisor.band(run("r", status: "completed", calls: [done]))?.state == nil)
        #expect(FlowAdvisor.band(run("r", status: "error", calls: [done]))?.state == nil)
        // The turn ended while a background sub-agent of the request still runs: it may still ask.
        let background = MightyGraphSupport.shownRuns([run("r", status: "completed", agents: [agent("bg", background: true)], calls: [done], output: "launched")])
        #expect(FlowAdvisor.band(background[0])?.state == .onCall)
        #expect(FlowAdvisor.band(run("r", status: "completed", calls: [call("b", at: 3, state: "consulting")]))?.state == .consulting)
    }

    @Test func aBandShowsTheNewestRowsItHasRoomForAndCountsTheRest() throws {
        let calls = (0..<6).map { call("c\($0)", at: $0, until: $0 + 1, text: "ok") }
        let band = try #require(FlowAdvisor.band(run("r", calls: calls)))
        let chrome = FlowAdvisor.headerHeight + FlowAdvisor.footerHeight
        // Room for all of them.
        let all = FlowAdvisor.visible(band, height: chrome + 6 * FlowAdvisor.rowHeight)
        #expect(all.rows.count == 6 && all.earlier == 0)
        // One row short: the line that counts the earlier ones takes room too.
        let fewer = FlowAdvisor.visible(band, height: chrome + 5 * FlowAdvisor.rowHeight)
        #expect(fewer.rows.map(\.id) == ["c2", "c3", "c4", "c5"] && fewer.earlier == 2)
        // The note takes its room first.
        let noted = try #require(FlowAdvisor.band(run("r", calls: calls.map { var value = $0; value.text = nil; return value })))
        #expect(FlowAdvisor.visible(noted, height: chrome + 6 * FlowAdvisor.rowHeight).rows.count == 4)
        #expect(FlowAdvisor.visible(noted, height: chrome + FlowAdvisor.noteHeight + 6 * FlowAdvisor.rowHeight).rows.count == 6)
        // Consultations no longer kept are counted with the ones that do not fit.
        let trimmed = try #require(FlowAdvisor.band(run("r", calls: calls, total: 40)))
        #expect(trimmed.calls == 40 && trimmed.earlier == 34)
        let shown = FlowAdvisor.visible(trimmed, height: chrome + 6 * FlowAdvisor.rowHeight)
        #expect(shown.rows.count == 5 && shown.earlier == 35)
        // No room at all: nothing is drawn past the band.
        #expect(FlowAdvisor.visible(band, height: 10).rows.isEmpty)
        // The least band holds one row with its note.
        #expect(FlowAdvisor.visible(noted, height: FlowAdvisor.minimumHeight).rows.count == 1)
    }

    // MARK: The diagram

    @Test func theBandStandsLeftOfItsRunFromTheMainSessionsBlockToTheRunsLastBlock() throws {
        let agents = [agent("a"), agent("b"), agent("c")]
        let advised = run("r", agents: agents, calls: [call("c1", at: 1, until: 2), call("c2", caller: "b", at: 3, state: "consulting")], configured: "opus")
        let graph = layout([advised])
        let band = try #require(node(graph, advised, FlowAdvisor.suffix))
        let work = try #require(node(graph, advised, MightyGraphLayout.workSuffix))
        let own = graph.nodes.filter { $0.id != band.id }
        #expect(band.content == .advisor(0))
        #expect(band.frame.width == FlowAdvisor.width)
        // From the main session's block's top down to the run's last block.
        #expect(band.frame.minY == work.frame.minY)
        #expect(band.frame.maxY == own.map(\.frame.maxY).max())
        // Left of every block it shares rows with, with its gap, and over none of them.
        let beside = own.filter { $0.frame.minY < band.frame.maxY && $0.frame.maxY > band.frame.minY }
        #expect(band.frame.maxX + FlowAdvisor.gap == beside.map(\.frame.minX).min())
        #expect(!own.contains { $0.frame.intersects(band.frame) })
        // The canvas reaches it.
        #expect(graph.originX <= band.frame.minX - MightyGraphCamera.margin)
        #expect(graph.originX + graph.size.width >= own.map(\.frame.maxX).max()! + MightyGraphCamera.margin)
        // It is a label beside the flow: not selected, not resized, never what a following camera keeps.
        #expect(band.isFixed && !band.isResizable && !band.isAuxiliary)
        #expect(MightyGraphFollow.targetID(graph, runs: [advised]) != band.id)
        // Nothing else moved: the same run without an advisor lays out the same blocks in the same places.
        var plain = advised; plain.advisor = nil; plain.configuredAdvisor = nil
        let before = layout([plain])
        #expect(before.nodes.map(\.id) == own.map(\.id))
        #expect(before.nodes.map(\.frame) == own.map(\.frame))
        #expect(before.edges.map(\.id) == graph.edges.map(\.id))
        #expect(before.advisorLinks.isEmpty)
        // Without the role view there is no band, whatever the run carries.
        let flat = layout([advised], fanOut: false)
        #expect(!flat.nodes.contains { $0.content == .advisor(0) } && flat.advisorLinks.isEmpty)
    }

    @Test func aDottedLinkGoesToEachBlockThatAskedAndPassesOverNoBlock() throws {
        let agents = [agent("a"), agent("b"), agent("c")]
        let calls = [call("c1", at: 1, until: 2), call("c2", caller: "a", at: 3, until: 4), call("c3", caller: "c", at: 5, until: 6), call("c4", caller: "c", at: 7, state: "consulting"),
                     call("c5", at: 8, until: 9)]
        let advised = run("r", agents: agents, calls: calls)
        let graph = layout([advised])
        let band = try #require(node(graph, advised, FlowAdvisor.suffix))
        let work = try #require(node(graph, advised, MightyGraphLayout.workSuffix))
        let first = try #require(node(graph, advised, "agent:a")), third = try #require(node(graph, advised, "agent:c"))
        // One link for each caller, in the order they first asked, however often they asked.
        #expect(graph.advisorLinks.map(\.target) == [work.id, first.id, third.id])
        let routes = graph.advisorRoutes()
        #expect(routes.count == 3)
        // Straight into the main session's block and into the first card of the row: nothing stands between.
        #expect(routes[0] == [CGPoint(x: band.frame.maxX, y: work.frame.minY + 14), CGPoint(x: work.frame.minX, y: work.frame.minY + 14)])
        #expect(routes[1] == [CGPoint(x: band.frame.maxX, y: first.frame.minY + 14), CGPoint(x: first.frame.minX, y: first.frame.minY + 14)])
        // The third card stands behind two others: along the gap above the row and down into its top by its corner.
        #expect(routes[2] == [CGPoint(x: band.frame.maxX, y: third.frame.minY - 6), CGPoint(x: third.frame.minX + 14, y: third.frame.minY - 6),
                              CGPoint(x: third.frame.minX + 14, y: third.frame.minY)])
        // No stretch of any link lies inside a block it does not end at.
        for (link, points) in zip(graph.advisorLinks, routes) {
            let others = graph.nodes.filter { $0.id != link.band && $0.id != link.target }.map(\.frame)
            for (from, to) in zip(points, points.dropFirst()) {
                let stretch = CGRect(x: min(from.x, to.x), y: min(from.y, to.y), width: abs(to.x - from.x), height: abs(to.y - from.y)).insetBy(dx: -0.5, dy: -0.5)
                #expect(!others.contains { $0.intersects(stretch) })
            }
        }
        // Outside the visible part nothing is routed.
        #expect(graph.advisorRoutes(in: CGRect(x: 5_000, y: 5_000, width: 10, height: 10)).isEmpty)
        // A caller under a folded card is laid out nowhere: its link goes to the card above it, once.
        let nested = [agent("outer"), agent("inner", parent: "outer")]
        let deep = run("d", agents: nested, calls: [call("n1", caller: "inner", at: 1, until: 2), call("n2", caller: "outer", at: 3, until: 4)])
        let folded = layout([deep])
        let outer = try #require(node(folded, deep, "agent:outer"))
        #expect(node(folded, deep, "agent:inner") == nil)
        #expect(folded.advisorLinks.map(\.target) == [outer.id])
        let opened = layout([deep], opened: [outer.id])
        #expect(opened.advisorLinks.map(\.target) == [MightyGraphLayout.nodeID(deep, suffix: "agent:inner"), outer.id])
    }

    @Test func eachRunHasItsOwnBandAndTheyNeverOverlap() throws {
        let first = run("one", status: "completed", agents: [agent("a", status: "completed"), agent("b", status: "completed")],
                        calls: [call("c1", at: 1, until: 2)], output: "done")
        let second = run("two", calls: (0..<12).map { call("d\($0)", at: 10 + $0, until: 11 + $0) }, configured: "opus")
        let third = run("three", output: nil)
        let graph = layout([first, second, third])
        let bands = graph.nodes.filter { if case .advisor = $0.content { true } else { false } }
        #expect(bands.map(\.content) == [.advisor(0), .advisor(1)])
        #expect(!bands[0].frame.intersects(bands[1].frame))
        for band in bands {
            #expect(!graph.nodes.contains { $0.id != band.id && $0.frame.intersects(band.frame) })
        }
        // A band is as tall as its run's column, never taller: the rows it has no room for are counted in it.
        let work = try #require(node(graph, second, MightyGraphLayout.workSuffix))
        #expect(bands[1].frame.minY == work.frame.minY && bands[1].frame.height == work.frame.height)
        let band = try #require(FlowAdvisor.band(second))
        let shown = FlowAdvisor.visible(band, height: bands[1].frame.height)
        #expect(shown.rows.count < 12 && shown.rows.count + shown.earlier == 12)
        #expect(shown.rows.last?.id == "d11")
    }

    // MARK: The session log

    @Test func theLogAndTheStatusLineReadTheAdvisor() throws {
        try LocaleOverride.$language.withValue(.ko) { () throws in
            let scout = agent("scout")
            let live = run("r", agents: [scout], calls: [call("c1", at: 2, until: 5), call("c2", caller: "scout", at: 6, state: "consulting")], model: "claude-opus-5-5")
            let band = MightyGraphLayout.nodeID(live, suffix: FlowAdvisor.suffix)
            let notes = FlowAdvisor.notes([live])
            #expect(notes.map(\.what) == ["메인의 요청으로 검토 시작", "조언 전달 · 내용 비표시", "scout의 요청으로 검토 시작"])
            #expect(notes.map(\.time) == [stamp(2), stamp(5), stamp(6)])
            #expect(notes.allSatisfy { $0.who == "조언자 · Opus 5.5" && $0.nodeID == band })
            #expect(Set(notes.map(\.id)).count == 3)
            #expect(FlowAdvisor.status([live]) == .consulting)
            #expect(FlowLog.status([live], advisor: FlowAdvisor.status([live]))?.text == "Sonnet 5.5 실행 중 · 하위 에이전트 1/1 실행 중 · 조언자 검토 중")
            // On call while the request runs, with or without a consultation so far.
            #expect(FlowAdvisor.status([run("r", calls: [call("c1", at: 2, until: 5)])]) == .onCall)
            #expect(FlowAdvisor.status([run("r", configured: "opus")]) == .onCall)
            #expect(FlowLog.status([run("r", configured: "opus")], advisor: .onCall)?.text == "Sonnet 5.5 실행 중 · 조언자 대기")
            // Once the activity ended: how often it was consulted, and nothing for an advisor never asked.
            #expect(FlowAdvisor.status([run("r", status: "completed", calls: [call("c1", at: 2, until: 5), call("c2", at: 6, until: 7)], output: "done")]) == .consulted(2))
            #expect(FlowAdvisor.status([run("r", status: "completed", configured: "opus", output: "done")]) == nil)
            #expect(FlowAdvisor.status([run("r")]) == nil && FlowAdvisor.status([]) == nil)
            // A request awaiting its background work's notice still has its advisor on call.
            let awaiting = MightyGraphSupport.shownRuns([run("r", status: "completed", agents: [agent("bg", background: true)], configured: "opus", output: "launched")])
            #expect(FlowAdvisor.status(awaiting) == .onCall)
            // Only the newest activity: an older request's advisor says nothing about a new one without.
            let old = run("old", status: "completed", calls: [call("o1", at: 1, until: 2)], output: "done")
            #expect(FlowAdvisor.status([old, run("new")]) == nil)
            #expect(FlowAdvisor.notes([old, run("new")]).isEmpty)
            // Of each run only its last few consultations are read for the log.
            let many = run("m", calls: (0..<20).map { (index: Int) in call("k\(index)", at: index, until: index) })
            let node = MightyGraphLayout.nodeID(many, suffix: FlowAdvisor.suffix)
            var expected: [String] = []
            for id in ["k17", "k18", "k19"] { expected += [node + ":" + id + ":start", node + ":" + id + ":end"] }
            #expect(FlowAdvisor.notes([many], limit: 3).map(\.id) == expected)
            #expect(FlowAdvisor.notes([many], limit: 3).count == 6)
        }
    }
}
