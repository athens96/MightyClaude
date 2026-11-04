import Foundation

/// The diagram's 진행 단계 panel (.omc/plans/progress-steps.md): which pane shows
/// the agent's checklist, and how each step reads. The same rules as the
/// Windows page's `mighty/steps.ts`.
public enum ProgressSteps {
    public enum State: Sendable, Equatable {
        case completed, inProgress, pending
        /// The state word a row's accessible label adds.
        public var labelKey: String {
            switch self {
            case .completed: return "graph.steps.status.completed"
            case .inProgress: return "graph.steps.status.inProgress"
            case .pending: return "graph.steps.status.pending"
            }
        }
    }
    public struct Row: Sendable, Equatable {
        public let text: String
        public let state: State
    }
    public struct Panel: Sendable, Equatable {
        public let done: Int
        public let total: Int
        public let rows: [Row]
        /// The pane runs: the step being worked on pulses.
        public let running: Bool
    }

    /// The panel for a pane, or nil when nothing is drawn: only a Claude or
    /// Codex agent pane (Antigravity keeps no checklist), only a list that has
    /// steps, and only while that list belongs to the pane's newest request
    /// (its `runId` is that request's graph run, by its `sourceRunID` or its
    /// id), so an older request's list never sits over a new one. `runs` are
    /// the diagram's runs. The step being worked on reads its activeForm while
    /// the pane runs.
    public static func panel(kind: String, provider: String, status: String, progress: TodoProgress?, runs: [MightyGraphRun]) -> Panel? {
        guard kind == SessionKind.claude, provider == "claude" || provider == "codex",
              let progress, !progress.items.isEmpty, let runId = progress.runId,
              let newest = MightyGraphSupport.newestRequestRun(runs), newest.sourceRunID == runId || newest.id == runId else { return nil }
        let running = status == "running"
        let rows = progress.items.map { item -> Row in
            let state: State = item.status == "completed" ? .completed : item.status == "in_progress" ? .inProgress : .pending
            return Row(text: state == .inProgress && running ? item.activeForm ?? item.content : item.content, state: state)
        }
        return Panel(done: rows.filter { $0.state == .completed }.count, total: rows.count, rows: rows, running: running)
    }
}
