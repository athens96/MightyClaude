import Testing
@testable import MightyCore

/// The diagram's 진행 단계 panel: which list shows, which row is current, what each row says.
struct ProgressStepsTests {
    private let list = TodoProgress(items: [
        TodoItem(content: "Implement the app", status: "completed"),
        TodoItem(content: "Build the app", activeForm: "Building the app", status: "in_progress"),
        TodoItem(content: "Deploy the app", activeForm: "Deploying the app", status: "pending"),
    ], runId: "process-2")
    /// An older request bound to its process, then the newest one.
    private let runs = [
        MightyGraphRun(id: "entry-1", status: "completed", sourceRunID: "process-1"),
        MightyGraphRun(id: "entry-2", status: "running", sourceRunID: "process-2"),
    ]

    @Test func theNewestRequestsListShowsWithItsCurrentStepWhileThePaneRuns() throws {
        let panel = try #require(ProgressSteps.panel(kind: "claude", provider: "claude", status: "running", progress: list, runs: runs))
        #expect(panel.done == 1 && panel.total == 3 && panel.running)
        #expect(panel.rows == [
            .init(text: "Implement the app", state: .completed),
            .init(text: "Building the app", state: .inProgress),
            .init(text: "Deploy the app", state: .pending),
        ])
        #expect(panel.rows.map(\.state.labelKey) == ["graph.steps.status.completed", "graph.steps.status.inProgress", "graph.steps.status.pending"])
        // A Codex pane shows its list the same way; a list named by the request's own block id matches too.
        var byBlock = list; byBlock.runId = "entry-2"
        #expect(ProgressSteps.panel(kind: "claude", provider: "codex", status: "running", progress: byBlock, runs: runs) == panel)
    }

    @Test func aPaneThatDoesNotRunReadsEachStepsOwnText() throws {
        let panel = try #require(ProgressSteps.panel(kind: "claude", provider: "claude", status: "completed", progress: list, runs: runs))
        #expect(!panel.running && panel.rows.map(\.text) == ["Implement the app", "Build the app", "Deploy the app"])
    }

    @Test func nothingIsDrawnForAnOlderRequestsListOrAnotherPane() {
        // The list belongs to an older request: a new request is on screen.
        var older = list; older.runId = "process-1"
        #expect(ProgressSteps.panel(kind: "claude", provider: "claude", status: "running", progress: older, runs: runs) == nil)
        // A list saved before lists named their run.
        var unnamed = list; unnamed.runId = nil
        #expect(ProgressSteps.panel(kind: "claude", provider: "claude", status: "running", progress: unnamed, runs: runs) == nil)
        // A sent request still waiting for its process is the newest: the previous list goes.
        let waiting = runs + [MightyGraphRun(id: "entry-3", status: "starting")]
        #expect(ProgressSteps.panel(kind: "claude", provider: "claude", status: "running", progress: list, runs: waiting) == nil)
        // A notice run after the request is not a request: the list stays.
        let notice = runs + [MightyGraphRun(id: "notice", status: "running", sourceRunID: "notice", noticeRun: true)]
        #expect(ProgressSteps.panel(kind: "claude", provider: "claude", status: "running", progress: list, runs: notice) != nil)
        #expect(ProgressSteps.panel(kind: "claude", provider: "claude", status: "running", progress: TodoProgress(items: [], runId: "process-2"), runs: runs) == nil)
        #expect(ProgressSteps.panel(kind: "claude", provider: "claude", status: "running", progress: nil, runs: runs) == nil)
        #expect(ProgressSteps.panel(kind: "claude", provider: "claude", status: "running", progress: list, runs: []) == nil)
        #expect(ProgressSteps.panel(kind: "claude", provider: "gemini", status: "running", progress: list, runs: runs) == nil)
        #expect(ProgressSteps.panel(kind: "shell", provider: "claude", status: "running", progress: list, runs: runs) == nil)
    }
}
