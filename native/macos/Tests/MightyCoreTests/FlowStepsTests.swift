import CoreGraphics
import Foundation
import Testing
@testable import MightyCore

/// A request's checklist as steps on its spine (`FlowSteps`): what each list change records and when, what a step holds,
/// what its card says, and where the layout puts it and what it launched.
struct FlowStepsTests {
    private let t0 = Date(timeIntervalSince1970: 1_800_000_000)
    private func at(_ seconds: Double) -> Date { t0.addingTimeInterval(seconds) }
    private func stamp(_ seconds: Double) -> String { FlowSteps.stamp(at(seconds)) }
    private func item(_ content: String, _ status: String, id: String? = nil) -> TodoItem {
        TodoItem(id: id, content: content, activeForm: content + " 중", status: status)
    }
    private func list(_ items: [TodoItem], run: String = "r") -> TodoProgress { TodoProgress(items: items, runId: run) }
    private func request(_ id: String = "r", status: String = "running", entries: [LogEntry] = [], agents: [MightyGraphAgent] = []) -> MightyGraphRun {
        MightyGraphRun(id: id, input: "릴리스", status: status, rootEntries: entries, agents: agents, sourceRunID: id, provider: "claude")
    }
    /// Step `index` of a thirty-step plan: twenty done, the twenty-first in progress, the rest not begun.
    private func longStep(_ index: Int) -> FlowStep {
        let status: String
        if index < 20 { status = "completed" } else if index == 20 { status = "in_progress" } else { status = "pending" }
        let started: String? = index <= 20 ? stamp(Double(index)) : nil
        return FlowStep(key: String(index), content: "단계 " + String(index), status: status, startedAt: started)
    }
    private func tool(_ id: String, _ summary: String, at seconds: Double) -> LogEntry {
        LogEntry(id: id, kind: "system", text: summary, timestamp: stamp(seconds), provider: "claude",
                 activity: AgentActivity(id: id, provider: "claude", kind: "command", state: "completed", toolName: "Bash", summary: summary))
    }

    // MARK: Recording

    @Test func eachChangeKeepsTheTimesOfTheStepsItHad() throws {
        var runs = [request()]
        FlowSteps.record(list([item("점검", "in_progress"), item("노트", "pending")]), into: &runs, previous: nil, at: at(0))
        FlowSteps.record(list([item("점검", "completed"), item("노트", "in_progress")]), into: &runs, previous: nil, at: at(10))
        FlowSteps.record(list([item("점검", "completed"), item("노트", "completed"), item("CI", "pending")]), into: &runs, previous: nil, at: at(25))
        let steps = try #require(runs[0].steps)
        #expect(steps.map(\.content) == ["점검", "노트", "CI"])
        #expect(steps.map(\.status) == ["completed", "completed", "pending"])
        #expect(steps[0].started == at(0) && steps[0].ended == at(10))
        #expect(steps[1].started == at(10) && steps[1].ended == at(25))
        #expect(steps[2].startedAt == nil && steps[2].endedAt == nil)
        #expect(steps.map(\.activeForm) == ["점검 중", "노트 중", "CI 중"])
    }

    @Test func aStepMarkedDoneWithoutGoingInProgressBeganWhenTheOneBeforeEnded() throws {
        var runs = [request()]
        FlowSteps.record(list([item("하나", "in_progress"), item("둘", "pending")]), into: &runs, previous: nil, at: at(0))
        FlowSteps.record(list([item("하나", "completed"), item("둘", "pending")]), into: &runs, previous: nil, at: at(5))
        FlowSteps.record(list([item("하나", "completed"), item("둘", "completed")]), into: &runs, previous: nil, at: at(9))
        let steps = try #require(runs[0].steps)
        #expect(steps[1].started == at(5) && steps[1].ended == at(9))
    }

    @Test func tasksAreFoundByTheirIdWhenTheirWordsChange() throws {
        var runs = [request()]
        FlowSteps.record(list([item("빌드", "in_progress", id: "1")]), into: &runs, previous: nil, at: at(0))
        FlowSteps.record(list([item("빌드와 서명", "in_progress", id: "1")]), into: &runs, previous: nil, at: at(4))
        let steps = try #require(runs[0].steps)
        #expect(steps.count == 1 && steps[0].content == "빌드와 서명" && steps[0].started == at(0))
    }

    @Test func theEarlierRequestsDoneTasksAreNeverThisRequestsSteps() throws {
        // Claude's task list outlives a request: the next request's first list still has the earlier one's done tasks.
        let earlier = TodoProgress(items: [item("이전 일", "completed", id: "1")], runId: "old")
        var runs = [request("old", status: "completed"), request("r")]
        FlowSteps.record(list([item("이전 일", "completed", id: "1"), item("새 일", "in_progress", id: "2")]), into: &runs, previous: earlier, at: at(0))
        #expect(runs[1].steps?.map(\.content) == ["새 일"])
        #expect(runs[0].steps == nil)
        // And stay out on the changes after it.
        FlowSteps.record(list([item("이전 일", "completed", id: "1"), item("새 일", "completed", id: "2")]), into: &runs, previous: runs[1].steps.map { _ in earlier }, at: at(3))
        #expect(runs[1].steps?.map(\.content) == ["새 일"])
    }

    @Test func aListNamedByNoRunOfThePaneChangesNothing() {
        var runs = [request()]
        FlowSteps.record(list([item("x", "in_progress")], run: "elsewhere"), into: &runs, previous: nil, at: at(0))
        #expect(runs[0].steps == nil)
    }

    @Test func thePaneKeepsTheStepsOnTheRequestAsItsEventsCome() throws {
        var session = RunSession(id: "s", workspaceId: "w", title: "t", kind: "claude", provider: "claude", model: "default", settings: RunSettings(), status: "running")
        session.graphRuns = [request()]
        let event = RunEvent(sessionId: "s", type: "todos", todos: list([item("점검", "in_progress")]))
        session.recordSteps(event, at: at(2))
        session.recordPlanMode(event)
        #expect(session.graphRuns?[0].steps?.first?.started == at(2))
        #expect(session.todoProgress?.items.count == 1)
        // A step a notice turn finishes is its request's.
        session.graphRuns?.append(MightyGraphRun(id: "r:notice:1", input: "", status: "running", sourceRunID: "r:notice:1", provider: "claude", noticeRun: true))
        session.recordSteps(RunEvent(sessionId: "s", type: "todos", todos: list([item("점검", "completed")])), at: at(8))
        #expect(session.graphRuns?[0].steps?.first?.ended == at(8))
        #expect(session.graphRuns?[1].steps == nil)
    }

    @Test func aRunWithStepsRoundTripsAndAnOlderSaveHasNone() throws {
        var run = request()
        run.steps = [FlowStep(key: "text:a", content: "a", status: "completed", startedAt: stamp(0), endedAt: stamp(1))]
        run.stepsCarried = ["id:9"]
        let back = try JSONDecoder().decode(MightyGraphRun.self, from: JSONEncoder().encode(run))
        #expect(back.steps == run.steps && back.stepsCarried == ["id:9"])
        let old = try JSONDecoder().decode(MightyGraphRun.self, from: Data(#"{"id":"o","input":"","status":"completed","rootEntries":[],"agents":[],"resultEntries":[]}"#.utf8))
        #expect(old.steps == nil)
    }

    // MARK: What a step holds

    @Test func aRecordBelongsToTheStepWhoseSpanHoldsIt() {
        let steps = [FlowStep(key: "a", content: "a", status: "completed", startedAt: stamp(10), endedAt: stamp(20)),
                     FlowStep(key: "b", content: "b", status: "in_progress", startedAt: stamp(20.4)),
                     FlowStep(key: "c", content: "c")]
        #expect(FlowSteps.stepIndex(at: at(5), in: steps) == nil)
        #expect(FlowSteps.stepIndex(at: at(10), in: steps) == 0)
        #expect(FlowSteps.stepIndex(at: at(15), in: steps) == 0)
        // Where two meet, the later; a step's start counts from its whole second.
        #expect(FlowSteps.stepIndex(at: at(20), in: steps) == 1)
        #expect(FlowSteps.stepIndex(at: at(99), in: steps) == 1)
    }

    @Test func aStepsCardSaysWhatItDidWithItsFiguresAndAPendingOneSaysItHasNotBegun() throws {
        var run = request(entries: [tool("e1", "git status", at: 1), tool("e2", "python scripts/release.py check", at: 3), tool("e3", "git push", at: 12)])
        run.steps = [FlowStep(key: "a", content: "점검", activeForm: "점검 중", status: "completed", startedAt: stamp(0), endedAt: stamp(10)),
                     FlowStep(key: "b", content: "푸시", activeForm: "푸시 중", status: "in_progress", startedAt: stamp(10)),
                     FlowStep(key: "c", content: "CI", status: "pending")]
        try LocaleOverride.$language.withValue(.ko) { () throws in
            let done = try #require(FlowSteps.card(0, of: 0, in: [run]))
            #expect(done.title == "1. 점검")
            #expect(done.status == "completed" && done.toolCalls == 2 && done.activity == "python scripts/release.py check")
            #expect(FlowCard.elapsed(done, now: at(99)) == StyleWidgetPresentation.elapsed(from: at(0), to: at(10)))
            let now = try #require(FlowSteps.card(1, of: 0, in: [run]))
            #expect(now.running && now.toolCalls == 1 && now.activity == "git push")
            let pending = try #require(FlowSteps.card(2, of: 0, in: [run]))
            #expect(pending.isPending && !pending.running && pending.stateWord == "예정" && pending.activity == nil && FlowCard.elapsed(pending, now: at(99)) == nil)
            #expect(FlowSteps.entries(0, of: 0, in: [run]).map(\.id) == ["e1", "e2"])
            // A notice turn's records are the activity's too.
            var notice = MightyGraphRun(id: "r:notice:1", input: "", status: "completed", rootEntries: [tool("n1", "gh run watch", at: 14)], sourceRunID: "r:notice:1", provider: "claude", noticeRun: true)
            notice.steps = nil
            #expect(FlowSteps.entries(1, of: 0, in: [run, notice]).map(\.id) == ["e3", "n1"])
        }
    }

    @Test func theExecutionCardNamesTheLeadAndCountsItsCalls() {
        var run = request(status: "completed", entries: [tool("e1", "ls", at: 1), tool("e2", "make", at: 2)])
        run.finalOutput = "완료했습니다.\n자세한 내용"
        let card = FlowSteps.leadCard(run, title: "실행 · 요청 1")
        #expect(card.title == "실행 · 요청 1" && card.toolCalls == 2 && card.activity == "완료했습니다." && card.proseActivity && !card.running)
    }

    @Test func aRunningExecutionCardReadsTheNewestThingTheSessionSaidOrDid() {
        let said = LogEntry(id: "s1", kind: "assistant", text: "먼저 상태를 봅니다.\n**화면을 찍어 보겠습니다.**", timestamp: stamp(2), provider: "claude")
        var run = request(entries: [tool("e1", "ls", at: 1), said])
        var card = FlowSteps.leadCard(run, title: "실행")
        #expect(card.activity == "화면을 찍어 보겠습니다." && card.proseActivity)
        // A call after it is newer: the line is that command, set as code.
        run.rootEntries.append(tool("e2", "screencapture -x shot.png", at: 3))
        card = FlowSteps.leadCard(run, title: "실행")
        #expect(card.activity == "screencapture -x shot.png" && !card.proseActivity)
    }

    // MARK: Layout

    private func layout(_ runs: [MightyGraphRun], opened: Set<String> = [], running: Bool = true) -> MightyGraphLayout {
        MightyGraphLayout.make(runs: MightyGraphSupport.shownRuns(runs), draft: "", running: running, expanded: [], splitsExecution: true, fanOut: .init(opened: opened))
    }

    @Test func theSpineRunsThroughTheStepsWithWhatEachLaunchedBesideIt() throws {
        var run = request(agents: [
            MightyGraphAgent(id: "early", title: "early", input: "", status: "completed", figures: GraphAgentFigures(startedAt: stamp(-5))),
            MightyGraphAgent(id: "notes", title: "notes", input: "", status: "running", background: true, figures: GraphAgentFigures(startedAt: stamp(12))),
        ])
        run.steps = [FlowStep(key: "a", content: "점검", status: "completed", startedAt: stamp(0), endedAt: stamp(10)),
                     FlowStep(key: "b", content: "노트", status: "in_progress", startedAt: stamp(10))]
        run.steps?.append(FlowStep(key: "c", content: "CI"))
        let graph = layout([run])
        let main = try #require(graph.nodes.first { $0.content == .request(0) })
        let work = try #require(graph.nodes.first { $0.content == .work(0) })
        let steps = graph.nodes.filter { if case .step = $0.content { true } else { false } }
        // The steps that began, on the request's centreline; the one not begun is only in the plan column.
        #expect(steps.map(\.content) == [.step(0, 0), .step(0, 1)])
        #expect((steps + [work]).allSatisfy { $0.isCompact && $0.frame.size == FlowSteps.cardSize && abs($0.frame.midX - main.frame.midX) < 0.5 })
        #expect(steps[0].frame.minY == main.frame.maxY + 32 && steps[1].frame.minY == steps[0].frame.maxY + 32)
        // Beside the request, what was launched before the first step; beside the second step, the agent launched in it.
        let early = try #require(graph.nodes.first { $0.id.hasSuffix("agent:early") })
        let notes = try #require(graph.nodes.first { $0.id.hasSuffix("agent:notes") })
        #expect(early.frame.minX == main.frame.maxX + FlowSteps.sideGap && early.frame.minY == main.frame.minY)
        #expect(notes.frame.minX == steps[1].frame.maxX + FlowSteps.sideGap && notes.frame.minY == steps[1].frame.minY)
        #expect(graph.edges.contains { $0.source == main.id && $0.target == early.id && $0.side })
        #expect(graph.edges.contains { $0.source == steps[1].id && $0.target == notes.id && $0.side })
        #expect(graph.edges.contains { $0.source == steps[0].id && $0.target == steps[1].id && !$0.side })
        // No delegate line in a spine; the main session's card stands where the answer will come, after the last step.
        #expect(!graph.nodes.contains { if case .delegate = $0.content { true } else { false } })
        #expect(!graph.nodes.contains { $0.content == .returning(0) })
        #expect(graph.edges.contains { $0.source == steps[1].id && $0.target == work.id && $0.joins })
        // The plan is no block of the diagram: the progress panel over it says it. A card beside a step names it on a line of its own.
        #expect(notes.frame.size == FlowCard.stepSize && early.frame.size == FlowCard.size)
        try LocaleOverride.$language.withValue(.ko) { () throws in
            #expect(FlowSteps.stepLabel(run.flowAgents.first { $0.id == "notes" }!, in: run) == "2. 노트")
            #expect(FlowSteps.stepLabel(run.flowAgents.first { $0.id == "early" }!, in: run) == nil)
        }
        for (index, first) in graph.nodes.enumerated() { for second in graph.nodes.dropFirst(index + 1) { #expect(!first.frame.intersects(second.frame), "\(first.id) × \(second.id)") } }
        // A side edge leaves the card's trailing edge and comes into the block's leading edge.
        let route = MightyGraphLayout.route(from: steps[1].frame, to: notes.frame, joins: false, side: true)
        #expect(route.first?.x == steps[1].frame.maxX && route.last?.x == notes.frame.minX)
        // Opened, a step is a block that can be resized, and the spine makes room for it.
        let opened = layout([run], opened: [steps[0].id])
        let block = try #require(opened.nodes.first { $0.id == steps[0].id })
        #expect(!block.isCompact && block.isResizable && block.frame.size == CGSize(width: 360, height: 240))
        #expect(opened.nodes.first { $0.id == steps[1].id }?.frame.minY == block.frame.maxY + 32)
    }

    @Test func theProgressPanelSaysTheRequestAndHowFarItsPlanGot() throws {
        var run = request()
        run.input = "릴리스 해줘\n자세히"
        run.steps = [FlowStep(key: "a", content: "점검", status: "completed", startedAt: stamp(0), endedAt: stamp(10)),
                     FlowStep(key: "b", content: "푸시", activeForm: "푸시하는 중", status: "in_progress", startedAt: stamp(10)),
                     FlowStep(key: "c", content: "CI")]
        try LocaleOverride.$language.withValue(.ko) { () throws in
            let progress = FlowSteps.progress(0, in: [run], heading: "요청 27")
            #expect(progress.title == "진행 상황" && progress.heading == "요청 27" && progress.request == "릴리스 해줘")
            #expect(progress.count == "1/3" && progress.running && progress.status == "running" && progress.stateWord == "실행 중")
            #expect(progress.rows.map(\.text) == ["1. 점검", "2. 푸시하는 중", "3. CI"])
            #expect(progress.rows.map(\.state) == [.done, .current, .pending])
            #expect(progress.rows[0].startedAt == at(0) && progress.rows[0].endedAt == at(10))
            // Stopped with the step in progress: it reads stopped, and ends when the activity did.
            var stopped = run; stopped.status = "stopped"
            let halted = FlowSteps.progress(0, in: [stopped], heading: "요청 27")
            #expect(halted.rows[1].state == .stopped && halted.stateWord == "중단" && !halted.running)
            // More steps than the panel holds: the one in progress stays in view and the rest are counted.
            var long = run
            long.steps = (0..<30).map(longStep)
            let window = FlowSteps.progress(0, in: [long], heading: "요청 1")
            #expect(window.rows.count == FlowSteps.progressVisibleRows - 1 && window.rows.contains { $0.state == .current })
            #expect(window.hidden + window.rows.count == 30)
            // A request without a plan: its words and its state, no rows.
            let plain = FlowSteps.progress(0, in: [request(status: "completed")], heading: "요청 2")
            #expect(plain.rows.isEmpty && plain.count == nil && plain.stateWord == "완료" && plain.request == "릴리스")
        }
    }

    @Test func aRequestWithoutStepsAndANoticeRunKeepTheRow() {
        let plain = layout([request(agents: [MightyGraphAgent(id: "a", title: "a", input: "", status: "running")])])
        #expect(plain.nodes.contains { if case .delegate = $0.content { true } else { false } })
        var notice = MightyGraphRun(id: "r:notice:1", input: "", status: "running", sourceRunID: "r:notice:1", provider: "claude", noticeRun: true)
        notice.steps = [FlowStep(key: "a", content: "a", status: "in_progress", startedAt: stamp(0))]
        #expect(FlowSteps.steps(notice).isEmpty)
    }

    @Test func aLogRowOfAStepGoesToItsCard() throws {
        var run = request(entries: [tool("e1", "git push", at: 12)])
        run.steps = [FlowStep(key: "a", content: "푸시", status: "in_progress", startedAt: stamp(10))]
        let rows = FlowLog.rows([run])
        #expect(rows.last?.nodeID == MightyGraphLayout.nodeID(run, suffix: FlowSteps.nodeSuffix(run.steps![0])))
    }

    @Test func aClaudeRunIsAskedForItsPlanAndWindowsAsksInTheSameWords() throws {
        let request = StartRunRequest(sessionId: "s", workspaceId: "w", input: "go")
        let args = try ProviderService.arguments(request, pluginDirectory: URL(fileURLWithPath: "/tmp"))
        let index = try #require(args.firstIndex(of: "--append-system-prompt"))
        #expect(args[index + 1] == FlowSteps.planInstruction)
        let allowed = try #require(args.firstIndex(of: "--allowedTools"))
        #expect(args[allowed + 1] == "TaskCreate,TaskUpdate,TaskList,TaskGet")
        #expect(FlowSteps.planInstruction.contains("TaskCreate") && !FlowSteps.planInstruction.contains("\n"))
        let windows = try String(contentsOf: GraphAgentIdentityTests.root.appendingPathComponent("native/windows/MightyCode.Core/FlowSteps.cs"), encoding: .utf8)
        #expect(windows.contains("PlanInstruction = \"" + FlowSteps.planInstruction.replacingOccurrences(of: "\"", with: "\\\"") + "\";"))
    }

    @Test func aFinishedRequestsPicturesHangOffItsRequestInTheRoleView() throws {
        var run = request(status: "completed")
        run.finalOutput = "done"
        let gallery = MightyGraphLayout.ImageGallery(runID: run.id, step: MightyGraphImages.requestStep, count: 2)
        let graph = MightyGraphLayout.make(runs: [run], draft: "", running: false, expanded: [], galleries: [gallery], splitsExecution: true, fanOut: .init())
        // No 실행 block once the answer is there: the pictures stand beside the request.
        #expect(!graph.nodes.contains { $0.content == .work(0) })
        let images = try #require(graph.nodes.first { if case .images = $0.content { true } else { false } })
        let main = try #require(graph.nodes.first { $0.content == .request(0) })
        #expect(images.frame.minY == main.frame.minY && images.frame.minX > main.frame.maxX)
    }

    @Test func aNodeIdNeverCarriesTheStepsWords() {
        let suffix = FlowSteps.nodeSuffix(FlowStep(key: "text:릴리스 노트 / 검토", content: "릴리스 노트 / 검토"))
        #expect(suffix.hasPrefix("step:") && suffix.dropFirst(5).allSatisfy { $0.isHexDigit })
        #expect(suffix == FlowSteps.nodeSuffix(FlowStep(key: "text:릴리스 노트 / 검토", content: "다른 말")))
    }
}
