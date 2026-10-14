import Foundation
import Testing
@testable import MightyCore

/// The 구조 view's kanban boards: one per request of a free pane, one per pipeline of a
/// styled pane, their header steps and columns, and the navigation between them.
struct MightyStructureBoardTests {
    typealias T = MightyStructureTests

    static func boards(_ runs: [MightyGraphRun], style: StyleEvaluator? = nil, phase: String? = nil, startingNew: Bool = false,
                       planHistory: [PlanRecord] = []) -> [MightyStructure.Board] {
        MightyStructure.boards(MightyStructure.make(runs: runs, style: style, currentPhase: phase.flatMap { style?.manifest.phase($0) },
                                                    startingNew: startingNew, planHistory: planHistory))
    }

    static func cardIDs(_ column: MightyStructure.BoardColumn) -> [String] { column.cards.map(\.id) }

    // MARK: Free boards

    @Test func aFreePaneHasOneBoardPerRequest() throws {
        let runs = [
            T.run("r1", input: "\n  첫 요청의 첫 줄\n둘째 줄", final: "끝났습니다"),
            T.run("r2", status: "running", agents: [T.agent("a", status: "running"), T.agent("b", parent: "a", status: "running"), T.agent("c")]),
            T.run("r3", status: "error"),
        ]
        let boards = Self.boards(runs)
        #expect(boards.map(\.id) == ["r1", "r2", "r3"])
        #expect(boards.map(\.ordinal) == [1, 2, 3])
        #expect(boards.allSatisfy { $0.kind == .request && $0.runIDs == [$0.id] })
        #expect(boards[0].headline == "첫 요청의 첫 줄")
        #expect(boards.map(\.status) == ["completed", "running", "error"])
        // The header: 요청 → 실행 → 결과, as a free row's steps.
        #expect(boards[0].steps.map(\.id) == ["request", "execution", "result"])
        #expect(boards[0].steps.allSatisfy { $0.title == nil })
        #expect(boards.map { $0.steps.map(\.step) } == [[.done, .done, .done], [.done, .current, .pending], [.done, .error, .error]])
        #expect(boards.map(\.currentStepIndex) == [2, 1, 2])
        // The columns: the request, then the 실행 card and one card per sub-agent, then the result.
        let running = boards[1]
        #expect(running.columns.map(\.id) == ["request", "execution", "result"])
        #expect(running.columns.map(\.step) == running.steps)
        let nodes = MightyStructure.clusters(runs[1]).flatMap(\.nodes)
        #expect(Self.cardIDs(running.columns[0]) == ["request:r2"])
        #expect(Self.cardIDs(running.columns[1]) == ["execution:r2"] + nodes.map { "agent:" + $0.nodeID })
        #expect(Self.cardIDs(running.columns[2]) == ["result:r2"])
        #expect(running.columns.map(\.count) == [1, 4, 0])
        #expect(boards[0].columns.map(\.count) == [1, 1, 1])
        // A nested sub-agent names the block it runs under; a root names none.
        let parents = running.columns[1].cards.compactMap { card -> String?? in
            if case .agent(_, let runID, let parent) = card { #expect(runID == "r2"); return parent }
            return nil
        }
        #expect(parents == [nil, nodes[0].title, nil])
    }

    // MARK: Pipelines

    @Test func aStyledPaneSplitsIntoPipelinesWhereThePhaseGoesBack() throws {
        let style = try T.style("ouroboros")
        let runs = [
            T.run("r1", input: "그냥 시작해줘"),            // goal (the default): the first request starts a pipeline
            T.run("r2", input: "ooo interview 할 일 앱"),   // interview
            T.run("r3", input: "좀 더 물어봐"),             // interview (the previous run's)
            T.run("r4", input: "ooo seed"),               // seed
            T.run("r5", input: "ooo pm 다시"),              // interview: back, a new pipeline
            T.run("r6", input: "이어서", status: "running"), // interview
        ]
        #expect(MightyStructure.phaseIDs(runs: runs, style: style) == ["goal", "interview", "interview", "seed", "interview", "interview"])
        let boards = Self.boards(runs, style: style)
        #expect(boards.map(\.id) == ["r1", "r5"])
        #expect(boards.map(\.runIDs) == [["r1", "r2", "r3", "r4"], ["r5", "r6"]])
        #expect(boards.allSatisfy { $0.kind == .pipeline })
        #expect(boards.map(\.ordinal) == [1, 2])
        #expect(boards[0].headline == "그냥 시작해줘")
        // Any request still running makes the pipeline running.
        #expect(boards.map(\.status) == ["completed", "running"])
        // An older pipeline that is over: every phase it reached is done, the rest pending.
        #expect(boards[0].steps.map(\.id) == style.manifest.orderedPhases.map(\.id))
        #expect(boards[0].steps.map(\.title) == style.manifest.orderedPhases.map(\.title))
        #expect(boards[0].steps.map(\.step) == [.done, .done, .done, .pending, .pending, .pending])
        // The newest pipeline stands where the pane does.
        #expect(boards[1].steps.map(\.step) == [.done, .current, .pending, .pending, .pending, .pending])
        #expect(boards[1].currentStepIndex == 1)
        // A single request is a pipeline of its own.
        #expect(Self.boards([runs[0]], style: style).map(\.runIDs) == [["r1"]])
        #expect(Self.boards([], style: style).isEmpty)
    }

    @Test func theNewestPipelineTakesThePanesPhaseOnlyWhenItLiesAhead() throws {
        let style = try T.style("ouroboros")
        let runs = [T.run("r1", input: "ooo interview 시작"), T.run("r2", input: "ooo seed")]
        // The pane moved on to 실행: the newest request takes it (`phaseIDs` rule 1).
        let moved = Self.boards(runs, style: style, phase: "run")
        #expect(moved.map(\.runIDs) == [["r1", "r2"]])
        #expect(moved[0].steps.map(\.step) == [.done, .done, .done, .current, .pending, .pending])
        // Starting anew at 목표: that phase is the next request's, so the pipeline stands at its last request.
        let fresh = Self.boards(runs, style: style, phase: "goal", startingNew: true)
        #expect(fresh.map(\.runIDs) == [["r1", "r2"]])
        #expect(fresh[0].steps.map(\.step) == [.done, .done, .current, .pending, .pending, .pending])
        // An older pipeline whose last request still runs stays current there.
        let busy = Self.boards([T.run("a", input: "ooo seed", status: "running"), T.run("b", input: "ooo interview")], style: style)
        #expect(busy.map(\.runIDs) == [["a"], ["b"]])
        #expect(busy[0].steps.map(\.step) == [.done, .done, .current, .pending, .pending, .pending])
    }

    @Test func claudePlanStagesSplitWhenARequestPlansAgain() throws {
        let style = try T.style("claude-plan")
        let runs = [
            T.run("r1", input: "로그인 화면 계획 세워줘", source: "s1"),
            T.run("r2", input: "테스트 돌려줘", source: "s2"),
            T.run("r3", input: "이제 다음 화면", source: "s3"),
            T.run("r4", input: "[계획] 새 기능", source: "s4"),
        ]
        let history = [T.record("p1", graphRunId: "s1", outcome: .approvedAuto), T.record("p3", graphRunId: "s3", outcome: .approvedConfirm)]
        #expect(MightyStructure.phaseIDs(runs: runs, style: style, planHistory: history) == ["execute", "plan", "execute", "plan"])
        let boards = Self.boards(runs, style: style, phase: "plan", planHistory: history)
        #expect(boards.map(\.runIDs) == [["r1"], ["r2", "r3"], ["r4"]])
        #expect(boards.map(\.id) == ["r1", "r2", "r4"])
        #expect(boards[1].columns.map { Self.cardIDs($0) } == [["summary:r2"], [], ["summary:r3"]])
        #expect(boards[1].columns.map(\.count) == [1, 0, 1])
        #expect(boards[1].steps.map(\.step) == [.done, .done, .done])
        #expect(boards[2].steps.map(\.step) == [.current, .pending, .pending])
    }

    @Test func aStyledColumnHoldsItsRequestsWithTheirSubAgentsUnderEach() throws {
        let style = try T.style("ouroboros")
        let runs = [
            T.run("r1", input: "ooo interview 시작", agents: [T.agent("x"), T.agent("y", parent: "x")]),
            T.run("r2", input: "더 물어봐"),
            T.run("r3", input: "ooo seed", status: "running"),
        ]
        let board = try #require(Self.boards(runs, style: style).first)
        let nodes = MightyStructure.clusters(runs[0]).flatMap(\.nodes).map { "agent:" + $0.nodeID }
        #expect(board.columns.map { Self.cardIDs($0) } == [[], ["summary:r1"] + nodes + ["summary:r2"], ["summary:r3"], [], [], []])
        #expect(board.columns.map(\.count) == [0, 2, 1, 0, 0, 0])
        #expect(board.steps.map(\.step) == [.done, .done, .current, .pending, .pending, .pending])
    }

    // MARK: Stable ids

    @Test func boardIDsHoldAsRunsArriveAndOldOnesAreTrimmed() throws {
        var runs = [T.run("r1"), T.run("r2")]
        #expect(Self.boards(runs).map(\.id) == ["r1", "r2"])
        runs.append(T.run("r3", status: "running"))
        #expect(Self.boards(runs).map(\.id) == ["r1", "r2", "r3"])
        #expect(Self.boards(Array(runs.dropFirst())).map(\.id) == ["r2", "r3"])

        let style = try T.style("ouroboros")
        var styled = [T.run("s1", input: "ooo interview 시작"), T.run("s2", input: "ooo seed"), T.run("s3", input: "ooo interview 다시")]
        let before = Self.boards(styled, style: style)
        #expect(before.map(\.id) == ["s1", "s3"])
        // A request that keeps the newest pipeline going joins it; a new pipeline is appended.
        styled.append(T.run("s4", input: "ooo seed"))
        #expect(Self.boards(styled, style: style).map(\.id) == ["s1", "s3"])
        #expect(Self.boards(styled, style: style).last?.runIDs == ["s3", "s4"])
        styled.append(T.run("s5", input: "ooo interview 또"))
        #expect(Self.boards(styled, style: style).map(\.id) == ["s1", "s3", "s5"])
        // Trims drop the oldest requests: the newer pipelines keep their ids, and the
        // oldest one, its first request gone, is named by its next.
        let trimmed = Self.boards(Array(styled.dropFirst()), style: style)
        #expect(trimmed.map(\.id) == ["s2", "s3", "s5"])
    }

    // MARK: Navigation

    @Test func theViewMovesOnWithTheNewestBoardUntilTheUserPicksAnOlderOne() {
        var runs = [T.run("r1"), T.run("r2"), T.run("r3", status: "running")]
        var nav = MightyStructure.Navigation()
        #expect(nav.index(in: []) == nil)
        #expect(nav.index(in: Self.boards(runs)) == 2)
        #expect(nav.onNewest(in: Self.boards(runs)))
        // A new request while on the newest board: the view moves to it.
        runs.append(T.run("r4", status: "running"))
        #expect(nav.index(in: Self.boards(runs)) == 3)
        #expect(!nav.hasUnseen(in: Self.boards(runs)))
        // Back one: the older board is kept by id as new ones arrive, and they are unseen.
        nav.step(-1, in: Self.boards(runs))
        #expect(nav.chosen == "r3")
        #expect(!nav.hasUnseen(in: Self.boards(runs)))
        runs.append(T.run("r5", status: "running"))
        #expect(nav.index(in: Self.boards(runs)) == 2)
        #expect(nav.hasUnseen(in: Self.boards(runs)))
        // Moving between older boards keeps the badge until the newest is shown.
        nav.select("r1", in: Self.boards(runs))
        #expect(nav.index(in: Self.boards(runs)) == 0)
        #expect(nav.hasUnseen(in: Self.boards(runs)))
        nav.step(-1, in: Self.boards(runs))
        #expect(nav.index(in: Self.boards(runs)) == 0)
        nav.select("missing", in: Self.boards(runs))
        #expect(nav.index(in: Self.boards(runs)) == 0)
        nav.showNewest(in: Self.boards(runs))
        #expect(nav.chosen == nil)
        #expect(nav.index(in: Self.boards(runs)) == 4)
        #expect(!nav.hasUnseen(in: Self.boards(runs)))
        nav.step(1, in: Self.boards(runs))
        #expect(nav.index(in: Self.boards(runs)) == 4)
        runs.append(T.run("r6", status: "running"))
        #expect(nav.index(in: Self.boards(runs)) == 5)
        // Stepping onto the newest board follows new ones again.
        nav.step(-2, in: Self.boards(runs))
        nav.step(1, in: Self.boards(runs))
        nav.step(1, in: Self.boards(runs))
        #expect(nav.chosen == nil)
    }

    @Test func aPickedBoardIsFoundAgainAfterItsFirstRequestIsTrimmed() throws {
        let style = try T.style("ouroboros")
        let runs = [T.run("s1", input: "ooo interview 시작"), T.run("s2", input: "ooo seed"),
                    T.run("s3", input: "ooo interview 다시"), T.run("s4", input: "ooo interview 또", status: "running")]
        var nav = MightyStructure.Navigation()
        nav.select("s1", in: Self.boards(runs, style: style))
        #expect(nav.index(in: Self.boards(runs, style: style)) == 0)
        let trimmed = Self.boards(Array(runs.dropFirst()), style: style)
        #expect(trimmed.map(\.id) == ["s2", "s3"])
        #expect(nav.index(in: trimmed) == 0)
        // A picked board trimmed away entirely: the oldest board left is shown.
        #expect(nav.index(in: Self.boards(Array(runs.dropFirst(2)), style: style)) == 0)
    }
}
