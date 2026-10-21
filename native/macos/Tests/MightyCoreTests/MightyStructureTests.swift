import Foundation
import Testing
@testable import MightyCore

/// The Mighty structure view (design "A. 단계별 열"): each request's phase, the phase
/// columns' states, a free pane's 요청 → 실행 → 결과 rows, and the sub-agent clusters.
struct MightyStructureTests {
    static func run(_ id: String, input: String? = nil, status: String = "completed", agents: [MightyGraphAgent] = [], final: String? = nil, source: String? = nil) -> MightyGraphRun {
        var run = MightyGraphRun(id: id, input: input ?? "do " + id, status: status, agents: agents, sourceRunID: source, finalOutput: final, provider: "claude")
        run.refreshResult()
        return run
    }

    static func agent(_ id: String, parent: String? = nil, status: String = "completed", kind: String? = nil) -> MightyGraphAgent {
        MightyGraphAgent(id: id, parentID: parent, title: "agent " + id, status: status, kind: kind)
    }

    static func record(_ id: String, graphRunId: String, outcome: PlanOutcome) -> PlanRecord {
        PlanRecord(id: id, runId: "channel-" + id, plan: "# plan", receivedAt: "2026-10-12T00:00:00.000Z", decidedAt: "2026-10-12T00:01:00.000Z",
                   outcome: outcome, graphRunId: graphRunId)
    }

    static func style(_ id: String) throws -> StyleEvaluator { try #require(BundledStyles.shared.evaluator(id)) }

    static func columns(_ shape: MightyStructure.Shape) -> [MightyStructure.Column] {
        if case .phases(let columns) = shape { return columns }
        Issue.record("expected phase columns")
        return []
    }

    static func rows(_ shape: MightyStructure.Shape) -> [MightyStructure.Row] {
        if case .free(let rows) = shape { return rows }
        Issue.record("expected free rows")
        return []
    }

    // MARK: Phase assignment

    @Test func runsTakeTheRecognisedPhaseElseThePreviousElseTheDefault() throws {
        let style = try Self.style("ouroboros")
        let runs = [
            Self.run("r1", input: "그냥 시작해줘"),            // nothing named: the style's default
            Self.run("r2", input: "ooo interview 할 일 앱"),   // an action with a phase
            Self.run("r3", input: "좀 더 물어봐"),             // free text: the previous run's
            Self.run("r4", input: "ooo status"),             // a phase-less action never moves the flow
            Self.run("r5", input: "ooo seed"),
            Self.run("r6", input: "ooo pm 다시"),              // an alias
        ]
        #expect(MightyStructure.phaseIDs(runs: runs, style: style) == ["goal", "interview", "interview", "interview", "seed", "interview"])
        #expect(MightyStructure.recognisedPhase("ooo status", style: style) == nil)
        #expect(MightyStructure.recognisedPhase("/ouroboros:evaluate", style: style) == "evaluate")
    }

    @Test func claudePlanRunsReadTheirStageAsThePaneReadsItsLatest() throws {
        let style = try Self.style("claude-plan")
        let runs = [
            Self.run("r1", input: "로그인 화면 계획 세워줘", source: "s1"),
            Self.run("r2", input: "테스트 돌려줘", source: "s2"),
            Self.run("r3", input: "이제 다음 화면", source: "s3"),
            Self.run("r4", input: "[계획] 새 기능", source: "s4"),
        ]
        let history = [
            Self.record("p1", graphRunId: "s1", outcome: .approvedAuto),
            // Sent back, then approved: the latest answer counts.
            Self.record("p3a", graphRunId: "s3", outcome: .revised),
            Self.record("p3b", graphRunId: "s3", outcome: .approvedConfirm),
        ]
        // Approved plans execute; a request with no plan answer is planning, as every
        // new request of the style starts in plan mode.
        #expect(MightyStructure.phaseIDs(runs: runs, style: style, planHistory: history) == ["execute", "plan", "execute", "plan"])
        // A record matched by the block id, as the diagram attaches it.
        #expect(MightyStructure.phaseIDs(runs: [Self.run("b1")], style: style,
                                         planHistory: [Self.record("p", graphRunId: "b1", outcome: .approvedConfirm)]) == ["execute"])
        // The pane's phase never pulls a run into a stage its own records do not say.
        #expect(MightyStructure.phaseIDs(runs: runs, style: style, currentPhase: style.manifest.phase("approve"), planHistory: history)
                == ["execute", "plan", "execute", "plan"])
    }

    @Test func claudePlanApprovedThenANewRequestWithoutAPlan() throws {
        let style = try Self.style("claude-plan")
        let runs = [Self.run("r1", source: "s1"), Self.run("r2", status: "running", source: "s2")]
        let history = [Self.record("p1", graphRunId: "s1", outcome: .approvedAuto)]
        // The pane: its latest request has no plan answer, so it is planning.
        let columns = Self.columns(MightyStructure.make(runs: runs, style: style, currentPhase: style.manifest.phase("plan"), planHistory: history))
        #expect(columns.map { $0.items.map(\.runID) } == [["r2"], [], ["r1"]])
        #expect(columns.map(\.step) == [.current, .pending, .done])
    }

    @Test func claudePlanExecutingAfterApproval() throws {
        let style = try Self.style("claude-plan")
        let runs = [Self.run("r1", status: "running", source: "s1")]
        let history = [Self.record("p1", graphRunId: "s1", outcome: .approvedAuto)]
        let columns = Self.columns(MightyStructure.make(runs: runs, style: style, currentPhase: style.manifest.phase("execute"), planHistory: history))
        #expect(columns.map { $0.items.map(\.runID) } == [[], [], ["r1"]])
        #expect(columns.map(\.step) == [.done, .done, .current])
    }

    @Test func claudePlanSentBackThenWaitingAgain() throws {
        let style = try Self.style("claude-plan")
        let runs = [Self.run("r0", source: "s0"), Self.run("r1", status: "running", source: "s1")]
        let history = [Self.record("p0", graphRunId: "s0", outcome: .approvedAuto), Self.record("p1", graphRunId: "s1", outcome: .revised)]
        // The revised plan's run asked again; that plan waits for an answer.
        let columns = Self.columns(MightyStructure.make(runs: runs, style: style, currentPhase: style.manifest.phase("approve"),
                                                        planHistory: history, pendingPlanRunID: "s1"))
        #expect(columns.map { $0.items.map(\.runID) } == [[], ["r1"], ["r0"]])
        #expect(columns.map(\.step) == [.done, .current, .done])
        // A pending plan whose run has drawn no block yet moves no card: the waiting
        // column is current and empty until it does.
        let early = Self.columns(MightyStructure.make(runs: [runs[0]], style: style, currentPhase: style.manifest.phase("approve"),
                                                      planHistory: history, pendingPlanRunID: "s-new"))
        #expect(early.map { $0.items.map(\.runID) } == [[], [], ["r0"]])
        #expect(early.map(\.step) == [.done, .current, .done])
    }

    @Test func startingNewLeavesFinishedCardsInDoneColumns() throws {
        let plan = try Self.style("claude-plan")
        let history = [Self.record("p1", graphRunId: "s1", outcome: .approvedAuto)]
        let columns = Self.columns(MightyStructure.make(runs: [Self.run("r1", source: "s1")], style: plan, currentPhase: plan.manifest.phase("plan"),
                                                        startingNew: true, planHistory: history))
        #expect(columns.map { $0.items.map(\.runID) } == [[], [], ["r1"]])
        #expect(columns.map(\.step) == [.current, .pending, .done])
        // A recognised-action style: the start phase is current, the requests stay where they were.
        let flow = try Self.style("ouroboros")
        let runs = [Self.run("r1", input: "ooo interview 앱"), Self.run("r2", input: "ooo seed")]
        let fresh = Self.columns(MightyStructure.make(runs: runs, style: flow, currentPhase: flow.manifest.phase("goal"), startingNew: true))
        #expect(fresh.map { $0.items.map(\.runID) } == [[], ["r1"], ["r2"], [], [], []])
        #expect(fresh.map(\.step) == [.current, .done, .done, .pending, .pending, .pending])
    }

    @Test func planRecordsOnlyMatterToAPlanStateStyle() throws {
        let style = try Self.style("ouroboros")
        let ids = MightyStructure.phaseIDs(runs: [Self.run("r1", input: "ooo seed", source: "s1"), Self.run("r2", source: "s2")], style: style,
                                           planHistory: [Self.record("p1", graphRunId: "s1", outcome: .approvedAuto)])
        #expect(ids == ["seed", "seed"])
    }

    // MARK: Columns

    @Test func theNewestCardSitsInThePanesPhase() throws {
        let style = try Self.style("ouroboros")
        let runs = [Self.run("r1", input: "ooo interview 앱"), Self.run("r2", input: "더 물어봐"), Self.run("r3", input: "ooo seed")]
        let columns = Self.columns(MightyStructure.make(runs: runs, style: style, currentPhase: style.manifest.phase("run")))
        #expect(columns.map(\.id) == ["goal", "interview", "seed", "run", "evaluate", "evolve"])
        // The pane stands in run (a state source may have moved it on): the newest card
        // goes there, so the bar and the cards agree.
        #expect(columns.map { $0.items.map(\.runID) } == [[], ["r1", "r2"], [], ["r3"], [], []])
        #expect(columns.map(\.step) == [.done, .done, .done, .current, .pending, .pending])
        // Oldest first under their phase.
        #expect(columns[1].items.map(\.ordinal) == [1, 2])
    }

    @Test func aColumnHoldingCardsIsNeverPending() throws {
        let style = try Self.style("ouroboros")
        // The flow went back to the interview after a seed.
        let runs = [Self.run("r1", input: "ooo seed"), Self.run("r2", input: "ooo interview 다시")]
        let columns = Self.columns(MightyStructure.make(runs: runs, style: style, currentPhase: style.manifest.phase("interview")))
        #expect(columns.map { $0.items.map(\.runID) } == [[], ["r2"], ["r1"], [], [], []])
        #expect(columns.map(\.step) == [.done, .current, .done, .pending, .pending, .pending])
    }

    @Test func withNoCurrentPhaseTheNewestRequestsPhaseIsCurrent() throws {
        // Four phases: brainstorm, plan, execute, finish.
        let style = StyleFixtures.statefulStyle.evaluator
        let runs = [Self.run("r1", input: "/loop:brainstorming 아이디어"), Self.run("r2", input: "/loop:writing-plans")]
        let columns = Self.columns(MightyStructure.make(runs: runs, style: style, currentPhase: nil))
        #expect(columns.map(\.step) == [.done, .current, .pending, .pending])
    }

    // MARK: Free panes

    @Test func freeRowsShowRequestRunAndResult() throws {
        let runs = [
            Self.run("running", status: "running"),
            Self.run("done", final: "끝났어요\n두 번째 줄"),
            Self.run("failed", status: "error"),
            Self.run("halted", status: "stopped"),
            // The request settled but a block still runs: the run is not over yet.
            Self.run("tail", agents: [Self.agent("bg", status: "running", kind: "task")]),
        ]
        // A style without phases is drawn as a free pane, as no style at all is.
        let catalogue = StyleFixtures.catalogueStyle.evaluator
        #expect(MightyStructure.make(runs: runs, style: catalogue, currentPhase: nil) == MightyStructure.make(runs: runs, style: nil, currentPhase: nil))
        let rows = Self.rows(MightyStructure.make(runs: runs, style: nil, currentPhase: nil))
        #expect(rows.map(\.id) == ["running", "done", "failed", "halted", "tail"])
        #expect(rows.map(\.request) == [.done, .done, .done, .done, .done])
        #expect(rows.map(\.execution) == [.current, .done, .error, .stopped, .current])
        #expect(rows.map(\.result) == [.pending, .done, .error, .stopped, .pending])
        #expect(rows.map(\.item.status) == ["running", "completed", "error", "stopped", "completed"])
        // The result on one line, and only once the request finished.
        #expect(rows[1].item.result == "끝났어요 두 번째 줄")
        #expect(rows[0].item.result == nil)
        #expect(rows[1].item.input == "do done")
        #expect(rows[1].item.nodeID == MightyGraphBlockSize.nodeID(runID: "done", suffix: "request"))
        #expect(rows[1].item.executionNodeID == MightyGraphBlockSize.nodeID(runID: "done", suffix: MightyGraphLayout.workSuffix))
    }

    @Test func theRequestPartHoldsTheInputAndTheExecutionPartTheWork() {
        var run = Self.run("r", input: "화면을 고쳐줘", status: "running")
        run.rootEntries = [LogEntry(id: "m1", kind: "assistant", text: "읽어 볼게요"), Self.tool("t1", summary: "a.swift")]
        let item = MightyStructure.item(run, index: 0)
        // The request part is the input alone.
        #expect(item.request?.text == "화면을 고쳐줘")
        #expect(item.input == "화면을 고쳐줘")
        // The 실행 part reads the main block's records, under the diagram's 실행 node.
        #expect(item.message?.text == "읽어 볼게요")
        #expect(item.activity.map(\.id) == ["t1"])
        #expect(item.status == "running")
        #expect(item.executionNodeID == MightyGraphBlockSize.nodeID(runID: "r", suffix: "execution"))
        #expect(item.executionNodeID != item.nodeID)
        // Both are nodes the diagram lays out, and its sub-agents branch from the 실행 one.
        var branched = run
        branched.agents = [Self.agent("a")]
        let laid = MightyGraphLayout.make(runs: [branched], draft: "", running: true, expanded: [], splitsExecution: true)
        let structure = MightyStructure.item(branched, index: 0)
        #expect(laid.nodes.contains { $0.id == structure.nodeID } && laid.nodes.contains { $0.id == structure.executionNodeID })
        #expect(laid.edges.contains { $0.source == structure.executionNodeID && $0.target == structure.clusters[0].nodes[0].nodeID })
        // A request with nothing yet still has its 실행 part, waiting.
        let fresh = MightyStructure.item(Self.run("n", status: "running"), index: 0)
        #expect(fresh.message == nil && fresh.activity.isEmpty && !fresh.finished)
    }

    @Test func theTallyCountsSubAgentsAndToolCalls() {
        var run = Self.run("r", agents: [Self.agent("a"), Self.agent("t", kind: "task"), Self.agent("s", kind: "steer")])
        let tool = AgentActivity(id: "x", provider: "claude", kind: "read", state: "completed", summary: "README.md")
        let turn = AgentActivity(id: "y", provider: "claude", kind: "turn", state: "completed", summary: "turn")
        run.rootEntries = [LogEntry(kind: "system", text: "read", activity: tool), LogEntry(kind: "system", text: "turn", activity: turn)]
        run.agents[0].entries = [LogEntry(kind: "system", text: "edit", activity: AgentActivity(id: "z", provider: "claude", kind: "edit", state: "running", summary: "a.swift"))]
        // A steer's records are not a sub-agent's work.
        run.agents[2].entries = [LogEntry(kind: "system", text: "read", activity: AgentActivity(id: "w", provider: "claude", kind: "read", state: "completed", summary: "b.swift"))]
        #expect(MightyStructure.item(run, index: 0).tally == MightyStructure.Tally(agents: 1, tools: 2))
    }

    // MARK: Content shown in place

    static func tool(_ id: String, kind: String = "read", state: String = "completed", summary: String) -> LogEntry {
        LogEntry(id: id, kind: "system", text: summary, activity: AgentActivity(id: "act-" + id, provider: "claude", kind: kind, state: state, summary: summary))
    }

    @Test func aRunningRequestShowsItsLatestMessageAndActivity() {
        var run = Self.run("r", input: "로그인 화면을 고쳐줘\n두 번째 줄", status: "running")
        run.rootEntries = [
            LogEntry(id: "m1", kind: "assistant", text: "먼저 읽어 볼게요"),
            Self.tool("t1", summary: "a.swift"),
            LogEntry(id: "turn", kind: "system", text: "turn", activity: AgentActivity(id: "turn", provider: "claude", kind: "turn", state: "running", summary: "turn")),
            Self.tool("t2", kind: "search", summary: "login"),
            Self.tool("t3", kind: "edit", summary: "b.swift"),
            Self.tool("t4", kind: "command", state: "running", summary: "swift test\n--filter Login"),
            LogEntry(id: "m2", kind: "assistant", text: "고치는 중이에요\n테스트를 돌립니다"),
            Self.tool("t5", kind: "web", summary: ""),
            Self.tool("t6", summary: "c.swift"),
            // A question's raw form is not a reply.
            LogEntry(id: "q", kind: "assistant", text: #"{"questions":[{"header":"H","question":"Q?","multiSelect":false,"options":[{"label":"A","description":""},{"label":"B","description":""}]}]}"#),
        ]
        let item = MightyStructure.item(run, index: 0)
        #expect(item.request?.text == "로그인 화면을 고쳐줘\n두 번째 줄")
        #expect(item.message?.text == "고치는 중이에요\n테스트를 돌립니다")
        #expect(item.message?.cut == false)
        #expect(item.message?.folds(lines: MightyStructure.messageLines, charactersPerLine: 20) == false)
        // The last five tool calls, oldest first, on one line each; the turn row and a
        // call with no words (nor a tool name) are not shown.
        #expect(item.activity.map(\.id) == ["t1", "t2", "t3", "t4", "t6"])
        #expect(item.activity.map(\.kind) == ["read", "search", "edit", "command", "read"])
        #expect(item.activity[3].text == "swift test --filter Login")
        #expect(item.activity[3].state == "running")
        #expect(item.answer == nil && item.result == nil)
    }

    @Test func aFinishedRequestShowsItsResultOnce() {
        let answer = "# 끝났어요\n\n- 로그인 수정\n- 테스트 통과"
        let run = Self.run("r", final: answer)
        // `refreshResult` put the answer in the main block too.
        #expect(run.rootEntries.last?.text == answer)
        let item = MightyStructure.item(run, index: 0)
        #expect(item.answer?.text == answer)
        #expect(item.answer?.preview == answer)
        #expect(item.message == nil)
        // A failed request has no answer; its last reply stays its message.
        var failed = Self.run("f", status: "error")
        failed.rootEntries = [LogEntry(id: "m", kind: "assistant", text: "권한이 없어요")]
        let failedItem = MightyStructure.item(failed, index: 0)
        #expect(failedItem.answer == nil)
        #expect(failedItem.message?.text == "권한이 없어요")
    }

    @Test func agentNodesCarryTheirPromptOutputAndActivity() {
        var agent = MightyGraphAgent(id: "a", title: "검토", input: "변경을 검토해 줘\n특히 보안", status: "running",
                                     responseRecords: [GraphResponseRecord(responseId: "x", usage: GraphTokenUsage()),
                                                       GraphResponseRecord(responseId: "y", model: "claude-opus-4-1", usage: GraphTokenUsage())])
        agent.entries = [Self.tool("a1", summary: "a.swift"), LogEntry(id: "o1", kind: "assistant", text: "문제 없음"),
                         Self.tool("a2", summary: "b.swift"), Self.tool("a3", summary: "c.swift"), Self.tool("a4", kind: "command", summary: "make")]
        var task = MightyGraphAgent(id: "t", parentID: "a", title: "빌드", input: "make all", status: "completed", kind: "task")
        task.entries = [LogEntry(id: "t-answer", kind: "assistant", text: "빌드 성공")]
        let nodes = MightyStructure.clusters(Self.run("r", status: "running", agents: [agent, task])).flatMap(\.nodes)
        #expect(nodes.map(\.agentID) == ["a", "t"])
        #expect(nodes[0].prompt?.text == "변경을 검토해 줘\n특히 보안")
        #expect(nodes[0].prompt?.folds(lines: MightyStructure.promptLines, charactersPerLine: 20) == false)
        #expect(nodes[0].output?.text == "문제 없음")
        #expect(nodes[0].activity.map(\.id) == ["a2", "a3", "a4"])
        #expect(nodes[0].model == "claude-opus-4-1")
        #expect(nodes[1].prompt?.text == "make all")
        #expect(nodes[1].output?.text == "빌드 성공")
        #expect(nodes[1].activity.isEmpty && nodes[1].model == nil)
    }

    @Test func shownTextsAreCappedAndFolded() {
        let long = String(repeating: "가", count: 3_000)   // 9,000 bytes
        let request = MightyStructure.excerpt(long, maximumBytes: MightyStructure.requestBytes, lines: MightyStructure.requestLines)
        #expect(request.map { $0.text.utf8.count <= MightyStructure.requestBytes } == true)
        #expect(request?.text.hasSuffix("…") == true)
        #expect(request?.cut == true)
        // A long line is cut in the preview, so a folded card never lays out the whole text.
        #expect(request.map { $0.preview.count <= MightyStructure.previewLineCharacters } == true)
        // Twenty lines: the first ones fold, the text keeps them all.
        let lines = (1...20).map { "line \($0)" }.joined(separator: "\n")
        let folded = MightyStructure.excerpt(lines, maximumBytes: MightyStructure.messageBytes, lines: MightyStructure.messageLines)
        #expect(folded?.text == lines)
        #expect(folded?.preview == (1...8).map { "line \($0)" }.joined(separator: "\n"))
        #expect(folded?.cut == true)
        // Short text fits as it is; nothing but blanks or controls is nothing.
        #expect(MightyStructure.excerpt("짧은 글", maximumBytes: 100, lines: 2) == MightyStructure.Excerpt(text: "짧은 글", preview: "짧은 글"))
        #expect(MightyStructure.excerpt(" \n\u{1B}[0m ", maximumBytes: 100, lines: 2) == nil)
        // The result and the sub-agent texts take their own caps.
        let big = String(repeating: "result ", count: 5_000)
        let item = MightyStructure.item(Self.run("r", input: long, final: big), index: 0)
        #expect(item.request.map { $0.text.utf8.count <= MightyStructure.requestBytes } == true)
        #expect(item.answer.map { $0.text.utf8.count <= MightyStructure.answerBytes && $0.text.hasSuffix("…") } == true)
        #expect(item.answer?.cut == true)
        var agent = MightyGraphAgent(id: "a", input: long, status: "completed")
        agent.entries = [LogEntry(id: "o", kind: "assistant", text: big)]
        let node = MightyStructure.clusters(Self.run("r2", agents: [agent]))[0].nodes[0]
        #expect(node.prompt.map { $0.text.utf8.count <= MightyStructure.promptBytes } == true)
        #expect(node.output.map { $0.text.utf8.count <= MightyStructure.outputBytes } == true)
    }

    @Test func aDeepSubAgentsTextIsNeverCutWithoutTheToggle() {
        // A node six levels deep in the narrowest column has 196pt for its text.
        let width = MightyStructure.nodeTextWidth(column: MightyStructure.minimumColumnWidth, depth: 6)
        #expect(width == 196)
        #expect(MightyStructure.nodeTextWidth(column: MightyStructure.minimumColumnWidth, depth: 60) == width)
        // About 17 Hangul syllables fit a 196pt line at 11pt; words wrap whole, so fewer are counted.
        let perLine = MightyStructure.charactersPerLine(width: width, fontSize: 11)
        #expect(perLine <= 17)
        // 120 syllables on one line take 8 lines of 17: past the 6 an output shows folded.
        let output = String(repeating: "가나다라마 ", count: 20)
        let excerpt = MightyStructure.excerpt(output, maximumBytes: MightyStructure.outputBytes, lines: MightyStructure.outputLines)
        #expect(excerpt?.cut == false)
        #expect(excerpt?.folds(lines: MightyStructure.outputLines, charactersPerLine: perLine) == true)
        // Any text that would wrap past its lines at the real width folds.
        for count in stride(from: 1, through: 200, by: 7) {
            let text = String(repeating: "가", count: count)
            let real = (count + 16) / 17
            let folds = MightyStructure.excerpt(text, maximumBytes: 4_096, lines: 6)?.folds(lines: 6, charactersPerLine: perLine) == true
            #expect(real <= 6 || folds, "\(count) syllables")
        }
        // The prompt's two lines at its smaller size, the same way.
        let prompt = MightyStructure.excerpt(String(repeating: "검토", count: 20), maximumBytes: MightyStructure.promptBytes, lines: MightyStructure.promptLines)
        #expect(prompt?.folds(lines: MightyStructure.promptLines, charactersPerLine: MightyStructure.charactersPerLine(width: width, fontSize: 10.5)) == true)
    }

    @Test func aFoldedAnswerKeepsWholeLinesAndClosesItsCodeFence() throws {
        let lines = ["# 결과", "", "```swift"] + (1...20).map { "let value\($0) = \($0)" } + ["```", "끝"]
        let answer = lines.joined(separator: "\n")
        let excerpt = try #require(MightyStructure.excerpt(answer, maximumBytes: MightyStructure.answerBytes, lines: MightyStructure.answerLines, markdown: true))
        #expect(excerpt.cut)
        // The first fifteen lines whole, and the fence they leave open closed.
        #expect(excerpt.preview == (lines.prefix(15) + ["```"]).joined(separator: "\n"))
        #expect(MightyStructure.openFence(excerpt.preview) == nil)
        // A tilde fence is closed with its own mark; a closed one is left alone.
        #expect(MightyStructure.openFence("~~~~\ncode") == "~~~~")
        #expect(MightyStructure.openFence("```\ncode\n```") == nil)
        // Lines stop at the preview's budget rather than being cut mid-line.
        let long = (1...15).map { "\($0) " + String(repeating: "x", count: 300) }.joined(separator: "\n")
        let wide = try #require(MightyStructure.excerpt(long, maximumBytes: MightyStructure.answerBytes, lines: MightyStructure.answerLines, markdown: true))
        #expect(wide.preview.split(separator: "\n").allSatisfy { $0.count == 302 || $0.count == 303 })
        #expect(wide.preview.count <= MightyStructure.answerLines * MightyStructure.previewLineCharacters)
        // A short answer is shown whole, with no toggle.
        #expect(MightyStructure.excerpt("**됐어요**", maximumBytes: 100, lines: 15, markdown: true)?.cut == false)
    }

    @Test func columnsShareThePanesWidthDownToTheMinimum() {
        // Three columns in 1,000pt with 12pt gaps: (1000 - 24) / 3.
        #expect(MightyStructure.columnWidth(available: 1_000, count: 3, gap: 12) == 325)
        // Too narrow: the minimum, and the view scrolls sideways.
        #expect(MightyStructure.columnWidth(available: 700, count: 6, gap: 12) == MightyStructure.minimumColumnWidth)
        #expect(MightyStructure.columnWidth(available: 2_000, count: 0, gap: 12) == MightyStructure.minimumColumnWidth)
    }

    // MARK: Clusters

    @Test func clustersAreTheRootTreesFlattenedDepthFirst() {
        let agents = [
            Self.agent("a"),
            Self.agent("b", parent: "a", status: "running"),
            Self.agent("c", parent: "b"),
            Self.agent("s", kind: "steer"),
            Self.agent("d", parent: "ghost"),          // an unknown parent is a root
            Self.agent("q", parent: "a", kind: "question"),
            Self.agent("t", parent: "a", kind: "task"),
            Self.agent("e", parent: "e"),              // its own parent is a root
            Self.agent("k", kind: "compact"),
            Self.agent("u", parent: "k"),              // under a left-out block: a root
        ]
        let clusters = MightyStructure.clusters(Self.run("r", agents: agents))
        #expect(clusters.map { $0.nodes.map(\.agentID) } == [["a", "b", "c", "t"], ["d"], ["e"], ["u"]])
        #expect(clusters[0].nodes.map(\.depth) == [0, 1, 2, 1])
        #expect(clusters[0].nodes.map(\.kind) == ["agent", "agent", "agent", "task"])
        #expect(clusters[0].nodes[1].status == "running")
        #expect(clusters[0].nodes[0].nodeID == MightyGraphBlockSize.nodeID(runID: "r", suffix: "agent:a"))
        #expect(clusters[0].nodes[0].title == "agent a")
        #expect(clusters[0].id == clusters[0].nodes[0].nodeID)
    }

    @Test func aRepeatedBlockIDIsKeptAtItsFirstPlace() {
        let agents = [Self.agent("a"), Self.agent("b", parent: "a"), Self.agent("a", parent: "b"), Self.agent("a")]
        let clusters = MightyStructure.clusters(Self.run("r", agents: agents))
        #expect(clusters.map { $0.nodes.map(\.agentIndex) } == [[0, 1]])
        #expect(Set(clusters.flatMap(\.nodes).map(\.id)).count == 2)
        #expect(MightyStructure.item(Self.run("r", agents: agents), index: 0).tally.agents == 2)
    }

    @Test func aCycleShowsEachBlockOnce() {
        let clusters = MightyStructure.clusters(Self.run("r", agents: [Self.agent("x", parent: "y"), Self.agent("y", parent: "x")]))
        #expect(clusters.map { $0.nodes.map(\.agentID) } == [["x", "y"]])
        #expect(clusters[0].nodes.map(\.depth) == [0, 1])
    }

    @Test func aDeepChainStopsAtTheCapAndGoesOnInANewCluster() {
        let agents = (0..<70).map { Self.agent("n\($0)", parent: $0 == 0 ? nil : "n\($0 - 1)") }
        let clusters = MightyStructure.clusters(Self.run("r", agents: agents))
        #expect(clusters.count == 2)
        #expect(clusters[0].nodes.count == MightyStructure.maximumDepth)
        #expect(clusters[0].nodes.last?.depth == MightyStructure.maximumDepth - 1)
        #expect(clusters[1].nodes.map(\.agentID) == (64..<70).map { "n\($0)" })
        #expect(clusters[1].nodes.first?.depth == 0)
        #expect(clusters.reduce(0) { $0 + $1.nodes.count } == 70)
    }

    // MARK: Rebuilding

    @Test func theSignatureMovesOnlyWithWhatTheViewDraws() throws {
        let style = try Self.style("ouroboros")
        var run = Self.run("r", status: "running", agents: [Self.agent("a", status: "running")])
        let before = MightyStructure.Signature(runs: [run], style: style, currentPhase: style.manifest.phase("goal"))
        #expect(before == MightyStructure.Signature(runs: [run], style: style, currentPhase: style.manifest.phase("goal")))
        // A streamed line may add a tool call.
        run.agents[0].entries.append(LogEntry(kind: "system", text: "read"))
        #expect(MightyStructure.Signature(runs: [run], style: style, currentPhase: style.manifest.phase("goal")) != before)
        #expect(before != MightyStructure.Signature(runs: [Self.run("r", status: "running", agents: [Self.agent("a", status: "completed")])], style: style,
                                                    currentPhase: style.manifest.phase("goal")))
        #expect(before != MightyStructure.Signature(runs: [Self.run("r", status: "running", agents: [Self.agent("a", status: "running")])], style: style,
                                                    currentPhase: style.manifest.phase("goal"), startingNew: true))
        #expect(before != MightyStructure.Signature(runs: [Self.run("r", status: "running", agents: [Self.agent("a", status: "running")])], style: style,
                                                    currentPhase: style.manifest.phase("goal"), pendingPlanRunID: "s"))
    }

    @Test func theSignatureFollowsTextThatGrowsInPlace() {
        var run = Self.run("r", status: "running", agents: [Self.agent("a", status: "running")])
        run.rootEntries = [LogEntry(id: "m", kind: "assistant", text: "읽는 중")]
        run.agents[0].entries = [Self.tool("t", state: "running", summary: "a.swift")]
        let before = MightyStructure.Signature(runs: [run], style: nil, currentPhase: nil)
        // A reply that grew by a few lines keeps its record and the count.
        var grown = run
        grown.rootEntries[0].text = "읽는 중" + String(repeating: "\n이제 고칩니다", count: 20)
        #expect(MightyStructure.Signature(runs: [grown], style: nil, currentPhase: nil) != before)
        // A tool call that finished, and a sub-agent's prompt that arrived.
        var finished = run
        finished.agents[0].entries[0].activity?.state = "completed"
        #expect(MightyStructure.Signature(runs: [finished], style: nil, currentPhase: nil) != before)
        var prompted = run
        prompted.agents[0].input = "검토해 줘"
        #expect(MightyStructure.Signature(runs: [prompted], style: nil, currentPhase: nil) != before)
    }

    @Test func theSignatureMovesEveryFewLinesNotEveryWord() {
        var run = Self.run("r", status: "running")
        run.rootEntries = [Self.tool("old", state: "running", summary: "swift build")] + (1...20).map { Self.tool("t\($0)", summary: "f\($0).swift") }
            + [LogEntry(id: "m", kind: "assistant", text: "가")]
        let before = MightyStructure.Signature(runs: [run], style: nil, currentPhase: nil)
        // A few more words in the streamed reply: nothing to rebuild yet.
        var words = run
        words.rootEntries[words.rootEntries.count - 1].text = "가 나다"
        #expect(MightyStructure.Signature(runs: [words], style: nil, currentPhase: nil) == before)
        // A few lines more: the card is built again.
        var lines = run
        lines.rootEntries[lines.rootEntries.count - 1].text = String(repeating: "한 줄의 글입니다\n", count: 12)
        #expect(MightyStructure.Signature(runs: [lines], style: nil, currentPhase: nil) != before)
        // A tool call older than the newest records finishing in place.
        var settled = run
        settled.rootEntries[0].activity?.state = "completed"
        #expect(MightyStructure.Signature(runs: [settled], style: nil, currentPhase: nil) != before)
    }

    @Test func theItemCacheRebuildsOnlyTheRequestThatMoved() {
        var runs = [Self.run("r1", final: "첫 결과"), Self.run("r2", status: "running")]
        var cache = MightyStructure.ItemCache()
        let first = cache.items(runs, signature: MightyStructure.Signature(runs: runs, style: nil, currentPhase: nil))
        #expect(first == runs.enumerated().map { MightyStructure.item($1, index: $0) })
        runs[1].rootEntries.append(LogEntry(id: "m", kind: "assistant", text: "진행 중"))
        let second = cache.items(runs, signature: MightyStructure.Signature(runs: runs, style: nil, currentPhase: nil))
        #expect(second[1].message?.text == "진행 중")
        #expect(second == runs.enumerated().map { MightyStructure.item($1, index: $0) })
        // The oldest request trimmed: a kept card takes its new number.
        let trimmed = Array(runs.dropFirst())
        let third = cache.items(trimmed, signature: MightyStructure.Signature(runs: trimmed, style: nil, currentPhase: nil))
        #expect(third == [MightyStructure.item(trimmed[0], index: 0)])
        #expect(third[0].ordinal == 1)
        // The shape built from cached cards is the shape built from scratch.
        #expect(MightyStructure.make(runs: trimmed, style: nil, currentPhase: nil, items: third) == MightyStructure.make(runs: trimmed, style: nil, currentPhase: nil))
    }

    // MARK: Empty pane and the saved choice

    @Test func anEmptyPaneHasNoRequests() throws {
        #expect(MightyStructure.make(runs: [], style: nil, currentPhase: nil) == .free([]))
        let style = try Self.style("ouroboros")
        let idle = Self.columns(MightyStructure.make(runs: [], style: style, currentPhase: nil))
        #expect(idle.count == 6 && idle.allSatisfy { $0.items.isEmpty && $0.step == .pending })
        let started = Self.columns(MightyStructure.make(runs: [], style: style, currentPhase: style.manifest.phase("goal")))
        #expect(started.map(\.step) == [.current, .pending, .pending, .pending, .pending, .pending])
    }

    @Test func theStructureChoiceRoundTrips() throws {
        var session = RunSession(workspaceId: "w", title: "t")
        session.graphViewMode = .structure
        let data = try JSONEncoder().encode(session)
        let wire = try JSONSerialization.jsonObject(with: data) as? [String: Any]
        #expect(wire?["graphViewMode"] as? String == "structure")
        let restored = try JSONDecoder().decode(RunSession.self, from: data)
        #expect(restored.mightyViewMode == .structure)
        // A build that does not know a word reads the pane as the diagram.
        let unknown = Data(#"{"id":"s","workspaceId":"w","graphViewMode":"structure2"}"#.utf8)
        #expect(try JSONDecoder().decode(RunSession.self, from: unknown).mightyViewMode == .diagram)
    }

    // MARK: Folded cards

    @Test func finishedRequestsFoldAndRunningOnesStayOpen() {
        typealias Fold = MightyStructure.Fold
        let running = MightyStructure.item(Self.run("a", status: "running"), index: 0)
        let waiting = MightyStructure.item(Self.run("w", status: "waiting"), index: 1)
        #expect(!Fold.isFolded(running, unfolded: []))
        #expect(!Fold.isFolded(waiting, unfolded: []))
        // The same request finishes: it folds by itself, with no event of its own.
        for status in ["completed", "error", "stopped"] {
            let finished = MightyStructure.item(Self.run("a", status: status), index: 0)
            #expect(Fold.isFolded(finished, unfolded: []), "\(status)")
        }
        // A finished request whose sub-agent still runs is not finished yet.
        let busy = MightyStructure.item(Self.run("b", agents: [Self.agent("x", status: "running")]), index: 0)
        #expect(!Fold.isFolded(busy, unfolded: []))
        let items = [running, MightyStructure.item(Self.run("c"), index: 1), MightyStructure.item(Self.run("d", status: "error"), index: 2)]
        #expect(Fold.folded(items, unfolded: ["d"]) == ["c"])
    }

    @Test func aCardOpenedByHandStaysOpenAndFoldsBackByHand() {
        typealias Fold = MightyStructure.Fold
        let finished = MightyStructure.item(Self.run("a"), index: 0)
        let opened = Fold.toggled(finished, unfolded: [])
        #expect(opened == ["a"])
        #expect(!Fold.isFolded(finished, unfolded: opened))
        // A later update of the same finished request keeps the user's choice.
        let again = MightyStructure.item(Self.run("a", final: "다시 끝났습니다"), index: 0)
        #expect(!Fold.isFolded(again, unfolded: opened))
        let closed = Fold.toggled(finished, unfolded: opened)
        #expect(closed.isEmpty)
        #expect(Fold.isFolded(finished, unfolded: closed))
        // A running card has no fold control: toggling it changes nothing, and it
        // still folds when it finishes.
        let running = MightyStructure.item(Self.run("b", status: "running"), index: 1)
        #expect(Fold.toggled(running, unfolded: []).isEmpty)
        #expect(Fold.isFolded(MightyStructure.item(Self.run("b"), index: 1), unfolded: Fold.toggled(running, unfolded: [])))
    }

    @Test func aFoldedCardSaysHowTheRequestEnded() {
        typealias Fold = MightyStructure.Fold
        let answered = MightyStructure.item(Self.run("a", final: "\n## **결론**: 끝났습니다\n\n- 둘째 줄"), index: 0)
        #expect(Fold.resultLine(answered) == "결론: 끝났습니다")
        // A failed or stopped request says so instead of its last words.
        #expect(Fold.resultLine(MightyStructure.item(Self.run("e", status: "error", final: "실패 이유"), index: 0)) == nil)
        #expect(Fold.resultLine(MightyStructure.item(Self.run("s", status: "stopped"), index: 0)) == nil)
        #expect(Fold.resultLine(MightyStructure.item(Self.run("n"), index: 0)) == nil)
        #expect(Fold.firstLine("```swift\n1. `let` x\n") == "let x")
        #expect(Fold.firstLine("> * 인용 목록") == "인용 목록")
        #expect(Fold.firstLine("  \n\t\n") == nil)
    }
}
