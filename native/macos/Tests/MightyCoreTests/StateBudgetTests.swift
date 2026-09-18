import Foundation
import Testing
@testable import MightyCore

struct StateBudgetTests {
    @Test func fairSharesLetSmallHistoriesKeepEverything() {
        #expect(StateRepository.fairShares(demands: [1_700_000, 240_000, 90_000, 40_000], total: 2_000_000) == [1_630_000, 240_000, 90_000, 40_000])
        // Estimates stay above the real charge, so an unconstrained profile is never trimmed.
        let run = MightyGraphRun(id: "shared-run", status: "completed", agents: [MightyGraphAgent(id: "child", status: "completed")])
        var budget = StateRepository.approximateGraphBytes([run])
        #expect(MightyGraphSupport.normalized([run], restoring: true, budget: &budget).count == 1 && budget > 0)
        #expect(StateRepository.fairShares(demands: [10, 20, 30], total: 1000) == [10, 20, 30])
        #expect(StateRepository.fairShares(demands: [500, 500, 500], total: 900) == [300, 300, 300])
        #expect(StateRepository.fairShares(demands: [], total: 100).isEmpty)
        #expect(StateRepository.fairShares(demands: [5, -1], total: 0) == [0, 0])
        #expect(StateRepository.approximateLogBytes([]) == 0 && StateRepository.approximateGraphBytes([]) == 0)
    }

    private func bigRun(_ index: Int) -> MightyGraphRun {
        let agents = (0..<8).map { MightyGraphAgent(id: "agent-\(index)-\($0)", input: String(repeating: "p", count: 16_000), status: "completed", entries: [LogEntry(kind: "assistant", text: String(repeating: "x", count: 60_000))]) }
        return MightyGraphRun(id: "run-\(index)", input: "Request \(index)", status: "completed", agents: agents, finalOutput: String(repeating: "f", count: 20_000))
    }

    @Test func laterSessionsKeepTheirGraphsNextToOneHugeSession() {
        let workspace = Workspace(id: "ws", name: "Repo", path: "/tmp/repo")
        var huge = RunSession(id: "huge", workspaceId: "ws", title: "Main", status: "completed", logs: [LogEntry(kind: "user", text: "go")])
        huge.graphRuns = (0..<12).map(bigRun)   // far more than the whole graph budget
        var small = RunSession(id: "small", workspaceId: "ws", title: "Other", status: "completed", logs: [LogEntry(kind: "user", text: "hi"), LogEntry(kind: "assistant", text: "hello")])
        small.graphRuns = [MightyGraphRun(id: "small-run", input: "hi", status: "completed", finalOutput: "hello")]
        let state = StateRepository.normalize(AppSnapshot(workspaces: [workspace], sessions: [huge, small]), restoring: true)
        let restoredSmall = state.sessions.first { $0.id == "small" }
        #expect(restoredSmall?.graphRuns?.map(\.id) == ["small-run"])
        let restoredHuge = state.sessions.first { $0.id == "huge" }
        #expect((restoredHuge?.graphRuns?.count ?? 0) < 12 && (restoredHuge?.graphRuns?.isEmpty == false))
        // Sessions that normalization drops (unknown workspace) take no share from real ones.
        var stray = huge; stray.id = "stray"; stray.workspaceId = "missing"
        let withStray = StateRepository.normalize(AppSnapshot(workspaces: [workspace], sessions: [stray, huge, small]), restoring: true)
        #expect(withStray.sessions.map(\.id) == ["huge", "small"])
        #expect(withStray.sessions.first { $0.id == "huge" }?.graphRuns?.count == restoredHuge?.graphRuns?.count)
        // Saving again does not lose what restoring kept.
        let saved = StateRepository.normalize(state, restoring: false)
        #expect(saved.sessions.first { $0.id == "small" }?.graphRuns?.map(\.id) == ["small-run"])
    }

    @Test func emptiedGraphHistoryFallsBackToTheLogsInsteadOfAnEmptyGraph() {
        let workspace = Workspace(id: "ws", name: "Repo", path: "/tmp/repo")
        var damaged = RunSession(id: "damaged", workspaceId: "ws", title: "Claude", status: "completed",
                                 logs: [LogEntry(kind: "user", text: "Fix the bug"), LogEntry(kind: "assistant", text: "Done.")])
        damaged.graphRuns = []   // what the old first-come budget left behind
        let state = StateRepository.normalize(AppSnapshot(workspaces: [workspace], sessions: [damaged]), restoring: true)
        let restored = state.sessions[0]
        #expect(restored.graphRuns == nil)
        #expect(restored.mightyGraphRuns.map(\.input) == ["Fix the bug"])
        #expect(restored.mightyGraphRuns.first?.finalOutput == "Done.")
        // A session that truly has nothing stays empty.
        let blank = RunSession(id: "blank", workspaceId: "ws", title: "Claude")
        var blankWithArray = blank; blankWithArray.graphRuns = []
        #expect(StateRepository.normalize(AppSnapshot(workspaces: [workspace], sessions: [blankWithArray]), restoring: true).sessions[0].graphRuns?.isEmpty == true)
    }
}
