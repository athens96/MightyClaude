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
        let style = try Self.style("superpowers")
        let runs = [Self.run("r1", input: "/superpowers:brainstorming 아이디어"), Self.run("r2", input: "/superpowers:writing-plans")]
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
        let paperthin = try Self.style("paperthin")
        #expect(MightyStructure.make(runs: runs, style: paperthin, currentPhase: nil) == MightyStructure.make(runs: runs, style: nil, currentPhase: nil))
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
}
