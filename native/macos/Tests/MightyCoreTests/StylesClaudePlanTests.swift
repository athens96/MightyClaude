import Foundation
import Testing
@testable import MightyCore

/// The "클러드 플랜" bundled style and the v6 vocabulary it is written in
/// (docs/mighty-styles.md §1.17): the plan-stage phase rule, the run-state
/// sources, the task-list widget, per-phase lines and the plan-mode launch.
struct StylesClaudePlanTests {
    private var style: RegisteredStyle { StyleFixtures.bundled("claude-plan") }
    private var evaluator: StyleEvaluator { style.evaluator }

    private static var manifestText: String {
        let url = StyleGolden.repositoryRoot.appendingPathComponent("native/macos/Sources/MightyCore/Resources/Styles/claude-plan.json")
        return (try? String(contentsOf: url, encoding: .utf8)) ?? ""
    }

    /// The bundled file with one exact piece replaced, and the code it is refused with.
    private func code(replacing old: String, with new: String) -> String? {
        let text = Self.manifestText
        #expect(text.contains(old), "fixture text not found: \(old)")
        return StyleFixtures.code(Data(text.replacingOccurrences(of: old, with: new).utf8))
    }

    private func pane(runs: [MightyGraphRun] = [], plans: [PlanRecord] = [], todos: TodoProgress? = nil,
                      background: BackgroundWork? = nil, status: String = "completed") -> RunSession {
        var session = RunSession(workspaceId: "ws", title: "plan", status: status)
        session.agentViewMode = "mighty"
        session.mightyStyle = "claude-plan"
        session.graphRuns = runs
        session.planHistory = plans
        session.todoProgress = todos
        session.backgroundWork = background
        return session
    }

    private func record(_ outcome: PlanOutcome, runId: String = "proc-1", graphRunId: String? = "proc-1", id: String = "req-1") -> PlanRecord {
        PlanRecord(id: id, runId: runId, plan: "# Plan", receivedAt: "2026-10-06T01:00:00.000Z",
                   decidedAt: "2026-10-06T01:01:00.000Z", outcome: outcome, graphRunId: graphRunId)
    }

    private func run(_ id: String, source: String?, status: String = "completed") -> MightyGraphRun {
        MightyGraphRun(id: id, input: "결제 모듈", status: status, sourceRunID: source)
    }

    private static let todos = TodoProgress(items: [
        TodoItem(content: "one", status: "completed"),
        TodoItem(content: "two", activeForm: "doing two", status: "in_progress"),
        TodoItem(content: "three", status: "pending"),
    ])

    // MARK: Manifest

    @Test func theBundledManifestDeclaresThePlanFlow() {
        let manifest = style.manifest
        #expect(style.source == .bundled && manifest.name == "클러드 플랜")
        #expect(manifest.orderedPhases.map(\.title) == ["계획", "승인", "실행"])
        #expect(manifest.launch == StyleLaunch(permissionMode: .plan))
        #expect(manifest.readsPlanState)
        #expect(manifest.prerequisites.probes.isEmpty && manifest.install == nil && manifest.autoAllow.isEmpty)
        #expect(manifest.stateSources?.runState == [
            StyleStateRunStateSource(source: .todos, widget: .progressBar),
            StyleStateRunStateSource(source: .todos, widget: .label),
            StyleStateRunStateSource(source: .background, widget: .label),
            StyleStateRunStateSource(source: .background, widget: .taskList),
        ])
        #expect(manifest.rules.enter == .verbatim)
        #expect(PlanCardSupport.styleDrawsTasks(manifest))
        #expect(!PlanCardSupport.styleDrawsTasks(StyleFixtures.bundled("superpowers").manifest))
    }

    @Test func theOtherBundledStylesDoNotReadThePlanStage() {
        for id in ["ouroboros", "paperthin", "superpowers"] {
            let manifest = StyleFixtures.bundled(id).manifest
            #expect(!manifest.readsPlanState && manifest.launch == nil)
        }
    }

    @Test func requestTitlesNameTheTwoActions() {
        #expect(evaluator.requestTitle(forInput: "[계획] 결제 모듈 정리") == "계획")
        #expect(evaluator.requestTitle(forInput: "[검증] 방금 한 작업") == "검증")
        #expect(evaluator.requestTitle(forInput: "결제 모듈 정리") == nil)
        #expect(evaluator.prompt(actionId: "new-plan", text: "  결제 모듈  ") == "[계획] 결제 모듈")
    }

    // MARK: Stage rules

    @Test func aPaneWithNothingAskedIsPlanning() {
        #expect(StyleStateEngine.planStage(session: pane(), pendingPlan: false) == .planning)
        #expect(StyleStateEngine.planStage(session: pane(runs: [run("g1", source: "proc-1", status: "running")]), pendingPlan: false) == .planning)
    }

    @Test func aWaitingPlanIsAwaitingApproval() {
        let session = pane(runs: [run("g1", source: "proc-1", status: "running")], plans: [record(.revised)])
        #expect(StyleStateEngine.planStage(session: session, pendingPlan: true) == .awaitingApproval)
    }

    @Test func anApprovedPlanOfTheLatestRequestIsExecuting() {
        for outcome in [PlanOutcome.approvedAuto, .approvedConfirm] {
            let running = pane(runs: [run("g1", source: "proc-1", status: "running")], plans: [record(outcome)], status: "running")
            #expect(StyleStateEngine.planStage(session: running, pendingPlan: false) == .executing)
            let finished = pane(runs: [run("g1", source: "proc-1")], plans: [record(outcome)], todos: Self.todos)
            #expect(StyleStateEngine.planStage(session: finished, pendingPlan: false) == .executing)
        }
    }

    @Test func aRevisedOrCancelledPlanIsPlanningAgain() {
        for outcome in [PlanOutcome.revised, .cancelled] {
            let session = pane(runs: [run("g1", source: "proc-1")], plans: [record(.approvedAuto, id: "old"), record(outcome, id: "new")])
            #expect(StyleStateEngine.planStage(session: session, pendingPlan: false) == .planning)
        }
    }

    @Test func theNextNewRequestStartsInPlanningAgain() {
        let session = pane(runs: [run("g1", source: "proc-1"), run("g2", source: "proc-2", status: "running")], plans: [record(.approvedAuto)])
        #expect(StyleStateEngine.planStage(session: session, pendingPlan: false) == .planning)
    }

    @Test func aRecordIsMatchedByTheRunsSourceIdTheBlockIdOrTheProcessRun() {
        // Both platforms write the run's source id into `graphRunId` (a Windows record's `runId` is the pane's id).
        let windows = pane(runs: [run("g1", source: "proc-1")], plans: [record(.approvedConfirm, runId: "pane", graphRunId: "proc-1")])
        #expect(StyleStateEngine.planStage(session: windows, pendingPlan: false) == .executing)
        let byBlock = pane(runs: [run("g1", source: "proc-1")], plans: [record(.approvedConfirm, runId: "pane", graphRunId: "g1")])
        #expect(StyleStateEngine.planStage(session: byBlock, pendingPlan: false) == .executing)
        let processOnly = pane(runs: [run("g1", source: "proc-1")], plans: [record(.approvedConfirm, graphRunId: nil)])
        #expect(StyleStateEngine.planStage(session: processOnly, pendingPlan: false) == .executing)
        let elsewhere = pane(runs: [run("g1", source: "proc-1")], plans: [record(.approvedConfirm, runId: "proc-9", graphRunId: "proc-9")])
        #expect(StyleStateEngine.planStage(session: elsewhere, pendingPlan: false) == .planning)
    }

    @Test func aRunningPaneWhoseNewBlockHasNotAppearedIsPlanning() {
        // The new run started, its block not there yet: the previous (approved) block is settled.
        var started = pane(runs: [run("g1", source: "proc-1")], plans: [record(.approvedAuto)], status: "running")
        #expect(StyleStateEngine.planStage(session: started, pendingPlan: false) == .planning)
        // A turn that is over and only waits on background work is still the approved request's.
        started.backgroundWork = BackgroundWork(tasks: [BackgroundTask(id: "b", kind: "agent", description: "x", startedAt: "2026-10-06T01:00:00.000Z")], turnEnded: true)
        #expect(StyleStateEngine.planStage(session: started, pendingPlan: false) == .executing)
    }

    @Test func aPaneWithoutSavedBlocksReadsItsLogLikeTheDiagramDoes() {
        // Never saved any block: the log's blocks have no source id, so no record is theirs.
        var legacy = pane(plans: [record(.approvedAuto)])
        legacy.graphRuns = nil
        legacy.logs = [LogEntry(kind: "user", text: "결제")]
        #expect(StyleStateEngine.planStage(session: legacy, pendingPlan: false) == .planning)
        // No block at all: the last record decides.
        legacy.logs = []
        #expect(StyleStateEngine.planStage(session: legacy, pendingPlan: false) == .executing)
        var empty = pane(plans: [record(.approvedAuto)])
        empty.graphRuns = []
        #expect(StyleStateEngine.planStage(session: empty, pendingPlan: false) == .executing)
    }

    @Test func thePhaseFollowsTheStageNotTheRequestHistory() {
        #expect(evaluator.currentPhase(prompts: ["[검증] x"])?.id == "plan")
        #expect(evaluator.currentPhase(planStage: .awaitingApproval)?.id == "approve")
        #expect(evaluator.currentPhase(prompts: ["[계획] x"], fileSourceStates: [:], planStage: .executing)?.id == "execute")
        let session = pane(runs: [run("g1", source: "proc-1")], plans: [record(.approvedAuto)])
        #expect(evaluator.currentPhase(session: session, fileSourceStates: [:], planStage: .executing)?.id == "execute")
        #expect(evaluator.currentPhase(session: session)?.id == "plan")
        // Another style is not touched by a stage.
        #expect(StyleFixtures.bundled("superpowers").evaluator.currentPhase(prompts: [], fileSourceStates: [:], planStage: .executing)?.id == "brainstorm")
    }

    // MARK: Chips, lines

    @Test func eachPhaseOffersItsOwnChipsAndLines() {
        let plan = style.manifest.phase("plan"), approve = style.manifest.phase("approve"), execute = style.manifest.phase("execute")
        #expect(evaluator.visibleActions(phase: plan, group: nil, running: false).map(\.id) == ["new-plan"])
        #expect(evaluator.visibleActions(phase: approve, group: nil, running: true).isEmpty)
        #expect(evaluator.visibleActions(phase: execute, group: nil, running: false).map(\.id) == ["new-plan", "verify"])
        #expect(evaluator.placeholder(phase: plan, running: false, answering: false) == "무엇을 계획할까요?")
        #expect(evaluator.placeholder(phase: approve, running: true, answering: false).hasPrefix("계획을 검토하고 승인하세요"))
        #expect(evaluator.placeholder(phase: approve, running: true, answering: true) == style.manifest.placeholders.answering)
        #expect(evaluator.guidanceLine(phase: approve, running: true)?.hasPrefix("계획을 검토하고 승인하세요") == true)
        #expect(evaluator.guidanceLine(phase: execute, running: false) == "실행을 마쳤습니다. 새 계획을 세우거나 검증을 요청하세요.")
        #expect(evaluator.guidanceLine(phase: execute, running: true) == "승인한 계획대로 실행 중입니다")
        // A phase without its own line for this state falls back to the style's.
        #expect(evaluator.placeholder(phase: approve, running: false, answering: false) == "무엇을 계획할까요?")
        #expect(evaluator.guidanceLine(phase: approve, running: false) == "승인 단계가 끝났습니다. 새 계획을 세우거나 검증을 요청하세요.")
        #expect(evaluator.enterBehaviour(draft: "결제", phase: plan, hasAttachments: false, running: false, hasRequests: false) == .verbatim)
    }

    @Test func theApprovalCardShowsTheLaunchAndThePlanState() {
        let sections = LocaleOverride.$language.withValue(.ko) { StyleApprovalCard.sections(style) }
        let launch = sections.first { $0.id == "launch" }
        #expect(launch?.foldable == false)
        #expect(launch?.lines.first?.contains("plan") == true)
        let state = sections.first { $0.id == "state" }?.lines ?? []
        #expect(state.contains("실행 상태 todos · 위젯 progressBar"))
        #expect(state.contains("실행 상태 background · 위젯 taskList"))
        #expect(state.contains("단계 approve ← 계획 상태 awaitingApproval"))
        #expect(sections.first { $0.id == "launch" } != nil)
        #expect(StyleApprovalCard.sections(StyleFixtures.bundled("superpowers")).first { $0.id == "launch" } == nil)
    }

    // MARK: Widgets

    @Test func theChecklistReadsOnlyWhileAPlanRuns() {
        let sources = style.manifest.stateSources!
        let input = StyleRunStateInput(planStage: .executing, todos: Self.todos)
        let executing = LocaleOverride.$language.withValue(.ko) { StyleStateEngine.reading(sources: sources, files: [], runEvents: [], runState: input) }
        #expect(executing.widgets.prefix(2) == [.progressBar(value: 1, total: 3), .label(text: "진행 중: doing two")])
        #expect(executing.planStage == .executing)
        for stage in [StylePlanStage.planning, .awaitingApproval] {
            var other = input; other.planStage = stage
            let reading = StyleStateEngine.reading(sources: sources, files: [], runEvents: [], runState: other)
            #expect(reading.widgets.prefix(2) == [.progressBar(value: 0, total: 0), .label(text: "")])
        }
        let list = StyleStateEngine.runState(StyleStateRunStateSource(source: .todos, widget: .list), input: input)
        #expect(list == .list(items: ["two", "three"]))
    }

    @Test func backgroundWorkDrawsAsLabelTaskListAndBar() {
        let work = BackgroundWork(tasks: [
            BackgroundTask(id: "a", kind: "shell", description: "npm test", startedAt: "2026-10-06T01:00:00.000Z", status: "completed", endedAt: "2026-10-06T01:00:40.000Z"),
            BackgroundTask(id: "b", kind: "agent", description: "review", startedAt: "2026-10-06T01:00:10.000Z"),
        ], turnEnded: true)
        let input = StyleRunStateInput(planStage: .executing, background: work)
        LocaleOverride.$language.withValue(.ko) {
            #expect(StyleStateEngine.runState(StyleStateRunStateSource(source: .background, widget: .label), input: input) == .label(text: "턴 완료 · 백그라운드 1개 실행 중"))
            var running = work; running.turnEnded = false
            #expect(StyleStateEngine.runState(StyleStateRunStateSource(source: .background, widget: .label),
                                              input: StyleRunStateInput(planStage: .executing, background: running)) == .label(text: "백그라운드 1개 실행 중"))
            #expect(StyleStateEngine.runState(StyleStateRunStateSource(source: .background, widget: .label),
                                              input: StyleRunStateInput(planStage: .executing)) == .label(text: ""))
        }
        // Running work first.
        guard case .taskList(let items) = StyleStateEngine.runState(StyleStateRunStateSource(source: .background, widget: .taskList), input: input) else {
            Issue.record("not a task list"); return
        }
        #expect(items.map(\.text) == ["review", "npm test"])
        #expect(items.last?.endedAt == "2026-10-06T01:00:40.000Z")
        #expect(StyleStateEngine.runState(StyleStateRunStateSource(source: .background, widget: .progressBar), input: input) == .progressBar(value: 1, total: 2))
    }

    @Test func aTaskListIsCappedAtEight() {
        let tasks = (0..<12).map { BackgroundTask(id: "t\($0)", kind: "agent", description: "task \($0)", startedAt: "2026-10-06T01:00:00.000Z") }
        let widget = StyleStateEngine.runState(StyleStateRunStateSource(source: .background, widget: .taskList),
                                               input: StyleRunStateInput(planStage: .planning, background: BackgroundWork(tasks: tasks)))
        guard case .taskList(let items) = widget else { Issue.record("not a task list"); return }
        #expect(items.count == StyleLimits.maximumTaskListItems)
    }

    @Test func aTaskRowSaysKindStatusAndElapsedTime() throws {
        try LocaleOverride.$language.withValue(.ko) {
            let presented = StyleWidgetPresentation.make(.taskList(items: [
                StylePanel.TaskItem(text: "review", kind: "agent", status: "running", startedAt: "2026-10-06T01:00:00.000Z"),
                StylePanel.TaskItem(text: " \n", kind: "weird", status: "nope", startedAt: "2026-10-06T01:00:00.000Z", endedAt: "2026-10-06T02:02:05.000Z"),
            ]))
            guard case .taskList(let tasks)? = presented else { Issue.record("not a task list"); return }
            #expect(tasks[0].kindTitle == "에이전트" && tasks[0].statusTitle == "실행 중" && tasks[0].running)
            let start = try #require(AgentRunTiming.parseTimestamp("2026-10-06T01:00:00.000Z"))
            #expect(tasks[0].elapsed(now: start.addingTimeInterval(45)) == "45초")
            #expect(tasks[0].elapsed(now: start.addingTimeInterval(192)) == "3분 12초")
            // An unknown kind and status read as the closed fallbacks; an empty description shows the kind.
            #expect(tasks[1].kind == "other" && tasks[1].text == "작업" && tasks[1].statusTitle == "알 수 없음")
            #expect(tasks[1].elapsed(now: start.addingTimeInterval(99_999)) == "1시간 2분")
            #expect(StyleWidgetPresentation.elapsed(from: start, to: start.addingTimeInterval(-5)) == "0초")
        }
        #expect(StyleWidgetPresentation.make(.taskList(items: []))?.isEmpty == true)
    }

    @Test func aTaskListRoundTripsThroughThePanelPayload() throws {
        let widget = StylePanel.Widget.taskList(items: [StylePanel.TaskItem(text: "review", kind: "agent", status: "running", startedAt: "2026-10-06T01:00:00.000Z")])
        let data = try JSONEncoder().encode(widget)
        let text = try #require(String(data: data, encoding: .utf8))
        #expect(text.contains("\"kind\":\"taskList\"") && !text.contains("endedAt"))
        #expect(try JSONDecoder().decode(StylePanel.Widget.self, from: data) == widget)
    }

    // MARK: Background outside the style

    @Test func theBackgroundStripShowsOutsideAStyleThatDrawsItsOwn() {
        let running = BackgroundWork(tasks: [BackgroundTask(id: "a", kind: "agent", description: "x", startedAt: "2026-10-06T01:00:00.000Z")])
        var waiting = running; waiting.turnEnded = true
        #expect(PlanCardSupport.showsBackgroundStrip(waiting, mighty: false, styleDrawsTasks: false))
        #expect(!PlanCardSupport.showsBackgroundStrip(running, mighty: false, styleDrawsTasks: false))
        #expect(PlanCardSupport.showsBackgroundStrip(running, mighty: true, styleDrawsTasks: false))
        #expect(!PlanCardSupport.showsBackgroundStrip(waiting, mighty: true, styleDrawsTasks: true))
        #expect(!PlanCardSupport.showsBackgroundStrip(nil, mighty: true, styleDrawsTasks: false))
        #expect(PlanCardSupport.backgroundTasks(waiting).map(\.text) == ["x"])
        LocaleOverride.$language.withValue(.ko) {
            #expect(PlanCardSupport.backgroundSummary(waiting) == "턴 완료 · 백그라운드 1개 실행 중")
            #expect(PlanCardSupport.backgroundSummary(running) == "백그라운드 1개 실행 중")
        }
    }

    // MARK: Validation (§1.17)

    @Test func thePlanStateRuleMustMapEveryStageToAPhase() {
        #expect(code(replacing: "\"map\": { \"planning\": \"plan\", \"awaitingApproval\": \"approve\", \"executing\": \"execute\" }",
                     with: "\"map\": { \"planning\": \"plan\", \"awaitingApproval\": \"approve\" }") == "E_RULE_INCOMPLETE")
        #expect(code(replacing: "\"executing\": \"execute\" }", with: "\"executing\": \"execute\", \"idle\": \"plan\" }") == "E_UNKNOWN_REFERENCE")
        #expect(code(replacing: "\"executing\": \"execute\" }", with: "\"executing\": \"run\" }") == "E_UNKNOWN_REFERENCE")
        #expect(code(replacing: "\"kind\": \"planState\"", with: "\"kind\": \"planStage\"") == "E_UNKNOWN_RULE")
    }

    @Test func runStateSourcesAreClosed() {
        #expect(code(replacing: "\"source\": \"todos\",      \"widget\": \"progressBar\"",
                     with: "\"source\": \"transcript\", \"widget\": \"progressBar\"") == "E_STATE_RUN_STATE")
        #expect(code(replacing: "\"source\": \"todos\",      \"widget\": \"label\"",
                     with: "\"source\": \"todos\", \"widget\": \"taskList\"") == "E_STATE_WIDGET")
        #expect(code(replacing: "\"source\": \"background\", \"widget\": \"label\"",
                     with: "\"source\": \"background\", \"widget\": \"list\"") == "E_STATE_WIDGET")
        #expect(code(replacing: "\"runState\": [", with: "\"files\": [{\"path\": \"a.md\", \"parser\": \"json\", \"widget\": \"taskList\"}], \"runState\": [") == "E_STATE_WIDGET")
        #expect(code(replacing: "\"source\": \"todos\",      \"widget\": \"progressBar\" }",
                     with: "\"source\": \"todos\", \"widget\": \"progressBar\", \"since\": 1 }") == "E_UNKNOWN_FIELD")
    }

    @Test func theLaunchModeIsClosed() {
        #expect(code(replacing: "\"permissionMode\": \"plan\"", with: "\"permissionMode\": \"fullAccess\"") == "E_UNKNOWN_RULE")
        #expect(code(replacing: "\"launch\": { \"permissionMode\": \"plan\" }", with: "\"launch\": { \"permissionMode\": \"plan\", \"model\": \"opus\" }") == "E_UNKNOWN_FIELD")
    }

    @Test func perPhaseLinesNameRealPhasesAndKeepTheTemplate() {
        #expect(code(replacing: "\"approve\": {\n        \"running\": \"계획을 검토하고 승인하세요 · 바꿀",
                     with: "\"review\": {\n        \"running\": \"계획을 검토하고 승인하세요 · 바꿀") == "E_UNKNOWN_REFERENCE")
        #expect(code(replacing: "\"running\": \"승인한 계획대로 실행 중입니다\"", with: "\"running\": \"{text} 실행 중\"") == "E_PROMPT_PLACEHOLDER")
        #expect(code(replacing: "\"running\": \"승인한 계획대로 실행 중입니다\"", with: "\"busy\": \"실행 중\"") == "E_UNKNOWN_FIELD")
    }

    @Test func theNewCodeIsInTheFrozenList() {
        #expect(StyleErrorCodes.all.contains("E_STATE_RUN_STATE"))
        #expect(StyleErrorCodes.all.count == 54)
    }
}
