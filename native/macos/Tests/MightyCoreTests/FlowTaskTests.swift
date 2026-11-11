import CoreGraphics
import Foundation
import Testing
@testable import MightyCore

/// The commands a Claude session runs in the background (`run_in_background` on Bash, task blocks) in the role view: they
/// stand in the fan-out row as cards with the sub-agents, in launch order, in the neutral ink; the delegate line, the
/// return node and the status line count them. Every other view leaves them to the strip and the notice runs, as before.
///
/// The recording is native/contracts/fixtures/claude-stream-background-commands.jsonl: Claude Code 2.1.322 with the
/// flags the app runs it with, two commands in the background, one that exits 0 and one that exits 3, and the turns
/// their notifications open.
struct FlowTaskTests {
    private func stamp(_ seconds: Int) -> String { ISO8601DateFormatter().string(from: Date(timeIntervalSince1970: 1_800_000_000 + Double(seconds))) }
    private func date(_ seconds: Int) -> Date { Date(timeIntervalSince1970: 1_800_000_000 + Double(seconds)) }
    private func task(_ id: String, command: String = "python3 scripts/release.py ci", title: String? = nil, status: String = "running", parent: String? = nil,
                      started: Int? = nil, ended: Int? = nil, exitCode: Int? = nil, source: String? = nil) -> MightyGraphAgent {
        let figures = GraphAgentFigures(startedAt: started.map(stamp), endedAt: ended.map(stamp), exitCode: exitCode)
        return MightyGraphAgent(id: id, parentID: parent, title: title ?? id, input: command, status: status, kind: "task", background: true,
                                sourceBlockId: source, figures: figures.isEmpty ? nil : figures)
    }
    private func agent(_ id: String, status: String = "running", background: Bool = false) -> MightyGraphAgent {
        MightyGraphAgent(id: id, title: id, input: "확인", status: status,
                         responseRecords: [GraphResponseRecord(responseId: id + "-m", model: "claude-haiku-5-5", usage: GraphTokenUsage(inputTokens: 1))],
                         background: background ? true : nil)
    }
    private func run(_ id: String, agents: [MightyGraphAgent], status: String = "running", output: String? = nil) -> MightyGraphRun {
        var run = MightyGraphRun(id: id, input: "요청 " + id, status: status, agents: agents, finalOutput: output,
                                 responseRecords: [GraphResponseRecord(responseId: id + "-m", model: "claude-sonnet-5-5", usage: GraphTokenUsage(inputTokens: 1))],
                                 provider: "claude")
        run.refreshResult()
        return run
    }
    private func replay() throws -> [MightyGraphRun] {
        var session = RunSession(id: "pane", workspaceId: "space", title: "Claude", provider: "claude")
        session.recordGraph(RunEvent(sessionId: "pane", type: "log", entry: LogEntry(id: "request-one", kind: "user", text: "probe", provider: "claude")))
        let box = GraphAgentIdentityTests.Box(session)
        let parser = CLIStreamParser(provider: "claude", log: { _, _ in }, resume: { _ in }, activityNamespace: "recorded",
                                     graph: { box.value.recordGraph(RunEvent(sessionId: "pane", type: "graph", graph: $0)) }, graphInput: "probe")
        for line in try GraphAgentIdentityTests.fixture("claude-stream-background-commands") { parser.push(line + Data([10])) }
        parser.flush(); parser.finishGraph(state: "completed")
        return box.value.mightyGraphRuns
    }

    // MARK: The recording

    @Test func theRecordedCommandsKeepTheirStartEndAndExitCode() throws {
        let runs = try replay()
        let request = try #require(runs.first)
        let tasks = request.agents.filter(\.isTask)
        #expect(tasks.map(\.title) == ["count to three", "fail on purpose"])
        #expect(tasks.map(\.input) == ["for i in 1 2 3; do echo line $i; sleep 1; done", "sleep 2; echo about to fail; exit 3"])
        #expect(tasks.allSatisfy { $0.background == true })
        #expect(tasks.map(\.status) == ["completed", "error"])
        // Started when the frame with the Bash call was written; ended when the CLI timed the end (`task_updated`'s
        // `end_time`, which comes right before the notification); the exit code as the notification's summary names it.
        #expect(tasks.map { $0.figures?.startedAt } == ["2026-11-11T02:08:51.382Z", "2026-11-11T02:08:51.646Z"])
        #expect(tasks.map { $0.figures?.endedAt } == ["2026-11-11T02:08:54.545Z", "2026-11-11T02:08:53.760Z"])
        #expect(tasks.map { $0.figures?.exitCode } == [0, 3])
        #expect(tasks.map { $0.figures?.toolCalls } == [nil, nil])
        // The notice run's copies carry the same figures.
        let copies = runs.filter { $0.noticeRun == true }.flatMap(\.agents).filter(\.isTask)
        #expect(Set(copies.compactMap(\.sourceBlockId)) == Set(tasks.map(\.id)))
        for copy in copies { #expect(copy.figures == tasks.first { $0.id == copy.sourceBlockId }?.figures) }

        // In the role view, both stand in the request's row as cards, in launch order.
        let shown = MightyGraphSupport.shownRuns(runs)
        #expect(shown[0].agents.isEmpty)
        #expect(shown[0].flowAgents.map(\.title) == ["count to three", "fail on purpose"])
        LocaleOverride.$language.withValue(.ko) {
            let cards = shown[0].flowAgents.map { FlowCard.content($0, in: shown[0]) }
            #expect(cards.map(\.identity) == ["작업 · for i in 1 2 3; do echo line $i; sleep 1; done", "작업 · sleep 2; echo about to fail; exit 3"])
            #expect(cards.map { FlowCard.figures($0, now: Date()) } == ["완료 · 종료 코드 0 · 3초", "실패 · 종료 코드 3 · 2초"])
            #expect(cards.allSatisfy { $0.isTask && $0.ink == .neutral && $0.marks == [.background] })
            // What they wrote, as the app reads it from their output files (the CLI adds the last line once they exit).
            let first = FlowCard.content(shown[0].flowAgents[0], in: shown[0], output: ["line 1", "line 2", "line 3", "", "[exited with code 0]"])
            #expect(first.activity == "line 3")
            // The branch and the way back while the main session still worked; the turn ended, so the result stands there now.
            let line = FlowDelegation.line(FlowDelegation.children(of: FlowDelegation.mainKey, in: shown[0]))
            #expect(line?.text == "작업 2개 실행")
            #expect(FlowReturn.state(shown[0])?.text == "메인으로 복귀 · 검토 중")
            let graph = MightyGraphLayout.make(runs: shown, draft: "", running: false, expanded: [], splitsExecution: true, fanOut: .init())
            let ids = shown[0].flowAgents.map { MightyGraphLayout.nodeID(shown[0], suffix: "agent:" + $0.id) }
            #expect(ids.allSatisfy { id in graph.nodes.first { $0.id == id }?.isCompact == true })
            // An ended command's card leads to its copy in the notice run.
            #expect(FlowCard.copyNodeID(of: shown[0].flowAgents[0], in: shown) != nil)
        }
    }

    // MARK: The card

    @Test func aCommandsCardIsItsDescriptionItsCommandItsLastLineAndItsState() throws {
        try LocaleOverride.$language.withValue(.ko) { () throws in
            let command = "python3 scripts/release.py ci\n--commit"
            let running = task("ci", command: command, title: "CI 대기", started: 0)
            let request = run("r", agents: [running])
            let card = FlowCard.content(running, in: request, output: ["native-macos 진행 중 · native-windows 진행 중"])
            #expect(card.title == "CI 대기")
            #expect(card.identity == "작업 · python3 scripts/release.py ci")
            #expect(card.activity == "native-macos 진행 중 · native-windows 진행 중")
            #expect(card.running && card.stateWord == "실행 중")
            #expect(FlowCard.figures(card, now: date(138)) == "실행 중 · 2분 18초")
            #expect(card.summary == "작업 · 백그라운드 · python3 scripts/release.py ci")
            #expect(card.nested == 0 && card.toolCalls == nil && card.tokens == nil)
            // Nothing written yet: no line rather than a made-up one.
            #expect(FlowCard.content(running, in: request).activity == nil)
            // Ended: how it exited, then how long it took.
            let done = task("ci", command: command, status: "completed", started: 0, ended: 840, exitCode: 0)
            #expect(FlowCard.figures(FlowCard.content(done, in: request), now: date(9_999)) == "완료 · 종료 코드 0 · 14분 0초")
            let failed = task("ci", command: command, status: "error", started: 0, ended: 75, exitCode: 1)
            #expect(FlowCard.figures(FlowCard.content(failed, in: request), now: date(9_999)) == "실패 · 종료 코드 1 · 1분 15초")
            // A command the CLI named no exit code for, and one whose end the app never saw.
            #expect(FlowCard.figures(FlowCard.content(task("x", status: "stopped", started: 0, ended: 5), in: request), now: date(9)) == "중단 · 5초")
            #expect(FlowCard.figures(FlowCard.content(task("x", status: "completed", exitCode: 0), in: request), now: date(9)) == "완료 · 종료 코드 0")
            // A block without a command still says what it is; without a description its title is its command.
            let bare = MightyGraphAgent(id: "b", title: "", input: "", status: "running", kind: "task", background: true)
            #expect(FlowCard.content(bare, in: request).identity == "작업")
            #expect(FlowCard.content(bare, in: request).title == "백그라운드 작업")
        }
    }

    @Test func theLastLineIsTheLastThingTheCommandWroteNotTheExitTrailer() {
        #expect(FlowCard.lastOutput([]) == nil)
        #expect(FlowCard.lastOutput(["one", "two"]) == "two")
        #expect(FlowCard.lastOutput(["one", "two", "", "   ", "[exited with code 0]"]) == "two")
        #expect(FlowCard.lastOutput(["[exited with code -1]"]) == nil)
        #expect(FlowCard.lastOutput(["  indented  "]) == "indented")
        // Only Claude Code's own trailer is left out.
        #expect(FlowCard.lastOutput(["[exited with code soon]"]) == "[exited with code soon]")
        #expect(FlowCard.lastOutput([String(repeating: "x", count: 500)])?.utf8.count ?? 0 <= 240)
    }

    @Test func aCommandsIdentityIsTheNeutralInkAndItsBackgroundMark() throws {
        try LocaleOverride.$language.withValue(.ko) { () throws in
            let who = try #require(FlowRoles.task(task("t", command: "eas build -p android --profile preview\nsecond line")))
            #expect(who.role == .task && who.ink == .neutral && who.marks == [.background])
            #expect(who.summary == "작업 · 백그라운드 · eas build -p android --profile preview")
            #expect(who.typeChip == nil && who.modelLabel == nil && who.effort == nil)
            #expect(FlowRoles.task(agent("a")) == nil)
            #expect(FlowRoles.roleName(.task) == "작업")
        }
    }

    // MARK: The line, the way back and the status

    @Test func theDelegateLineCountsCommandsBesideSubagents() throws {
        try LocaleOverride.$language.withValue(.ko) { () throws in
            // Both kinds: how many run side by side, how many of each, how many still run; no model.
            let notes = agent("release-notes", status: "completed", background: true)
            let mixed = try #require(FlowDelegation.line([notes, task("ci"), task("apk")]))
            #expect(mixed.text == "3개 병렬 · 하위 에이전트 1 · 작업 2 · 2/3 실행 중")
            #expect(mixed.head == "3개 병렬" && mixed.model == nil && mixed.progress == "2/3 실행 중")
            #expect(mixed.parts == [.init(role: .subagent, text: "하위 에이전트 1"), .init(role: .task, text: "작업 2")])
            #expect(mixed.count == 3 && mixed.running == 2)
            // Commands alone.
            #expect(FlowDelegation.line([task("ci"), task("apk", status: "completed")])?.text == "작업 2개 실행 · 1/2 실행 중")
            #expect(FlowDelegation.line([task("ci", status: "completed")])?.text == "작업 1개 실행")
            #expect(FlowDelegation.line([task("ci")])?.parts.isEmpty == true)
            // Sub-agents alone: as before.
            #expect(FlowDelegation.line([agent("a"), agent("b", status: "completed")])?.text == "하위 에이전트 2개에 위임 · Haiku 5.5 · 1/2 실행 중")
        }
        try LocaleOverride.$language.withValue(.en) { () throws in
            let mixed = try #require(FlowDelegation.line([agent("a", status: "completed"), task("ci"), task("apk")]))
            #expect(mixed.text == "3 in parallel · sub-agents 1 · tasks 2 · 2/3 running")
            #expect(FlowDelegation.line([task("ci")])?.text == "Running 1 task · 1/1 running")
        }
    }

    @Test func theWayBackCountsCommandsAndSaysSo() throws {
        try LocaleOverride.$language.withValue(.ko) { () throws in
            let blocks = [agent("notes", status: "completed", background: true), task("ci"), task("apk")]
            let waiting = try #require(FlowReturn.state(run("r", agents: blocks).shown))
            #expect(waiting.text == "병렬 2/3 실행 중 · 보고를 기다리는 중")
            #expect(waiting.total == 3 && waiting.running == 2)
            #expect(waiting.subagents == 1 && waiting.subagentsRunning == 0 && waiting.tasks == 2 && waiting.tasksRunning == 2)
            // Commands alone have a way back too.
            #expect(FlowReturn.state(run("r", agents: [task("dev")]).shown)?.text == "병렬 1/1 실행 중 · 보고를 기다리는 중")
            // Everything ended while the main session works on: back to it, as before.
            let ended = [agent("notes", status: "completed", background: true), task("ci", status: "completed")]
            #expect(FlowReturn.state(run("r", agents: ended).shown)?.text == "메인으로 복귀 · 검토 중")
            // Sub-agents alone: as before.
            #expect(FlowReturn.state(run("r", agents: [agent("a"), agent("b", status: "completed")]))?.text == "하위 에이전트 1/2 실행 중 · 보고를 기다리는 중")
            // A notice run's copy is not the request's work.
            #expect(FlowReturn.state(MightyGraphRun(id: "r:notice:1", agents: [task("c", status: "completed", source: "ci")], noticeRun: true)) == nil)
        }
    }

    @Test func theStatusLineSaysHowManyCommandsRun() throws {
        try LocaleOverride.$language.withValue(.ko) { () throws in
            // The mock's moment: the notes are done, CI and the APK build run.
            let release = MightyGraphSupport.shownRuns([run("r", agents: [agent("notes", status: "completed", background: true), task("ci"), task("apk")])])
            let status = try #require(FlowLog.status(release, advisor: .onCall))
            #expect(status.text == "Sonnet 5.5 실행 중 · 작업 2개 실행 중 · 조언자 대기")
            #expect(status.parts.map(\.role) == [.lead, .task, .advisor])
            // Sub-agents and commands running side by side.
            let both = MightyGraphSupport.shownRuns([run("r", agents: [agent("notes", background: true), task("ci"), task("apk", status: "completed")])])
            #expect(FlowLog.status(both)?.text == "Sonnet 5.5 실행 중 · 하위 에이전트 1/1 실행 중 · 작업 1개 실행 중")
            // Once nothing of the row runs, the sub-agents that ended are worth their word again; ended commands say nothing.
            let quiet = MightyGraphSupport.shownRuns([run("r", agents: [agent("notes", status: "completed", background: true), task("ci", status: "completed")])])
            #expect(FlowLog.status(quiet)?.text == "Sonnet 5.5 실행 중 · 하위 에이전트 1개 끝남")
            // The turn ended while the commands run: the request awaits their notices.
            let awaiting = MightyGraphSupport.shownRuns([run("r", agents: [task("ci")], status: "completed", output: "launched")])
            #expect(FlowLog.status(awaiting)?.text == "Sonnet 5.5 알림 대기 · 작업 1개 실행 중")
        }
    }

    // MARK: The diagram

    @Test func theMocksRowHoldsTheNotesAgentAndBothCommandsUnderOneLine() throws {
        try LocaleOverride.$language.withValue(.ko) { () throws in
            let request = run("r", agents: [agent("notes", status: "completed", background: true), task("ci", title: "CI 대기"), task("apk", title: "폰 APK 빌드")])
            let runs = MightyGraphSupport.shownRuns([request])
            let graph = MightyGraphLayout.make(runs: runs, draft: "", running: true, expanded: [], splitsExecution: true, fanOut: .init())
            func node(_ suffix: String) -> MightyGraphLayout.Node? { graph.nodes.first { $0.id == MightyGraphLayout.nodeID(request, suffix: suffix) } }
            let cards = try ["agent:notes", "agent:ci", "agent:apk"].map { try #require(node($0)) }
            #expect(cards.allSatisfy { $0.isCompact })
            #expect(Set(cards.map(\.frame.minY)).count == 1)
            #expect(cards[0].frame.maxX < cards[1].frame.minX && cards[1].frame.maxX < cards[2].frame.minX)
            let line = try #require(node(MightyGraphLayout.delegateSuffix + FlowDelegation.mainKey))
            #expect(graph.edges.filter { $0.source == line.id }.map(\.target) == cards.map(\.id))
            let back = try #require(node(MightyGraphLayout.returnSuffix))
            #expect(Set(graph.edges.filter { $0.target == back.id }.map(\.source)) == Set(cards.map(\.id)))
            #expect(FlowReturn.state(runs[0])?.text == "병렬 2/3 실행 중 · 보고를 기다리는 중")
            // The edges into the running commands' cards and into their line are live; into the ended agent's, not.
            let live = graph.liveFlowIDs(MightyGraphLayout.liveNodeIDs(runs, fanOut: true))
            #expect(live.contains(cards[1].id) && live.contains(cards[2].id) && !live.contains(cards[0].id) && live.contains(line.id))
            // Every other view draws what it drew: no command block in the request.
            #expect(runs[0].agents.isEmpty)
            let plain = MightyGraphLayout.make(runs: runs, draft: "", running: true, expanded: [], splitsExecution: true)
            #expect(!plain.nodes.contains { $0.id == cards[1].id })
        }
    }

    @Test func aCommandASubagentStartedStandsUnderThatSubagent() throws {
        let scout = agent("scout")
        let request = run("r", agents: [scout, task("watch", parent: "scout")])
        let runs = MightyGraphSupport.shownRuns([request])
        #expect(FlowDelegation.children(of: FlowDelegation.mainKey, in: runs[0]).map(\.id) == ["scout"])
        #expect(FlowDelegation.children(of: "scout", in: runs[0]).map(\.id) == ["watch"])
        // The sub-agent's card folds what is under it; opened, its own row holds the command's card.
        let scoutID = MightyGraphLayout.nodeID(request, suffix: "agent:scout"), watchID = MightyGraphLayout.nodeID(request, suffix: "agent:watch")
        let folded = MightyGraphLayout.make(runs: runs, draft: "", running: true, expanded: [], splitsExecution: true, fanOut: .init())
        #expect(!folded.nodes.contains { $0.id == watchID })
        let opened = MightyGraphLayout.make(runs: runs, draft: "", running: true, expanded: [], splitsExecution: true, fanOut: .init(opened: [scoutID]))
        let watch = try #require(opened.nodes.first { $0.id == watchID })
        #expect(watch.isCompact)
    }
}
