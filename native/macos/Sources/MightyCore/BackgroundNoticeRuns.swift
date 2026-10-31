import Foundation

/// Finished background work gets a diagram of its own. When a root-level
/// `<task-notification>` (or a background sub-agent's hand-back) opens a Claude
/// turn, the graph tracker records that turn as a new run, `<original>:notice:<n>`:
/// its main block names the finished work and holds Claude's whole reply, and
/// its children are copies of the finished background blocks
/// (`ExecutionGraphNode.sourceBlockId`). The request that started the work keeps
/// its background blocks as data, but no view draws them there.
public enum BackgroundNoticeRuns {
    public static let marker = ":notice:"
    /// The run id of the original run's `index`th notice run (from 1).
    public static func runID(original: String, index: Int) -> String { original + marker + String(index) }
    public static func isNoticeRun(_ runID: String) -> Bool { runID.contains(marker) }
    /// What opened a notice run; it names the run's kind while the run holds no copied block.
    public enum Opener: String, Sendable { case work, message, delegation }
    /// The longest name a notice run's title keeps; a longer one is cut to one less and "…".
    public static let nameLimit = 32
    /// A notice run's heading when it has no title of its own (older saves), where a request has "요청 n".
    public static var genericTitle: String { L("graph.background.noticeRun") }
    /// The kind a notice run's title opens with: finished work, a sub-agent's message, or a delegation notice.
    public static func kind(_ opener: Opener) -> String {
        switch opener {
        case .work: L("graph.background.noticeRun")
        case .message: L("graph.background.messageRun")
        case .delegation: L("graph.block.delegationNotice")
        }
    }
    /// "<kind> · <name>", "<kind> · <first> 외 <n>개", or "<kind>" alone. Each name keeps its first line, cut to
    /// `nameLimit`; equal names are counted, not merged.
    public static func title(kind: String, names: [String]) -> String {
        let lines = names.map { ($0.components(separatedBy: .newlines).first ?? "").trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
        guard let first = lines.first else { return kind }
        let short = first.count > nameLimit ? String(first.prefix(nameLimit - 1)) + "…" : first
        let name = lines.count == 1 ? short : L("graph.background.noticeRunMore", ["first": short, "count": "\(lines.count - 1)"])
        return L("graph.background.noticeRunTitle", ["kind": kind, "name": name])
    }
    /// A notice run's heading: its own title, or the generic one (older saves).
    public static func heading(_ run: MightyGraphRun) -> String { heading(title: run.noticeTitle) }
    /// The heading for a carried notice title (`MightyTimeline.Group.noticeTitle`, `MightyStructure.Item.noticeTitle`):
    /// the title trimmed, or the generic one when it is absent or blank.
    public static func heading(title: String?) -> String {
        guard let title = title?.trimmingCharacters(in: .whitespacesAndNewlines), !title.isEmpty else { return genericTitle }
        return title
    }
    /// The symbol in front of a notice run's heading: the task block's (`MightyGraphView.agentPresentation`).
    public static let icon = "terminal"
    /// The 실행 block's title: "실행 · 요청 n", or plain "실행" in a notice run, which is no request.
    public static func executionTitle(notice: Bool, ordinal: Int) -> String {
        notice ? L("graph.block.execution") : L("graph.block.executionOf", ["n": "\(ordinal)"])
    }
    /// The run id `noticeRun` is a notice run of: the request whose tracker opened it, or nil for a request.
    public static func originalRunID(_ noticeRun: String) -> String? {
        noticeRun.range(of: marker).map { String(noticeRun[..<$0.lowerBound]) }
    }

    /// One background task or sub-agent of a request awaiting its notices: its block, title and status (running, or how it ended).
    public struct AwaitedTask: Sendable, Equatable {
        public var id: String
        public var title: String
        public var status: String
        public init(id: String, title: String, status: String) { self.id = id; self.title = title; self.status = status }
        /// Its notice is in: the work ended (and told Claude so), whatever its outcome.
        public var delivered: Bool { MightyGraphSupport.terminal(status) }
        /// What its row says: 끝나면 알림 while it runs, 알림 전달됨 once it ended, 실패 or 중단됨 when it ended so.
        public var word: String {
            switch status {
            case "error": L("graph.block.taskNotice.failed")
            case "stopped": L("graph.block.taskNotice.stopped")
            case "completed": L("plan.background.noticeDelivered")
            default: L("plan.background.awaitingNotice")
            }
        }
    }
    /// A request awaiting the notices of the background work it started (`awaitingNotice`): every such task, in block order.
    public struct Awaiting: Sendable, Equatable {
        public var tasks: [AwaitedTask]
        public init(tasks: [AwaitedTask]) { self.tasks = tasks }
        public var delivered: Int { tasks.filter(\.delivered).count }
        /// "알림 {delivered}/{total}".
        public var progress: String { L("graph.block.awaitingNotice.progress", ["delivered": "\(delivered)", "total": "\(tasks.count)"]) }
    }
    /// The one awaiting-notice rule: a request whose own turn ended completed while background work it started (its tasks and
    /// background sub-agents) still runs, or ended with a notice turn not answered yet: a notice run of this request that copies
    /// the block has not ended. It ends when every notice turn was answered; a failed or stopped request, or one whose process
    /// ended (its blocks and notice runs stopped), is not awaiting. Nil otherwise and for a notice run. `runs` are the pane's
    /// runs, for the notice runs; a run `shownRuns` made carries what it read of the run it was made from.
    public static func awaitingNotice(_ run: MightyGraphRun, in runs: [MightyGraphRun]) -> Awaiting? {
        if let carried = run.carriedAwaiting { return carried }
        guard run.noticeRun != true, run.status == "completed" else { return nil }
        let tasks = run.agents.filter { $0.isTask || $0.isBackgroundAgent }
        guard !tasks.isEmpty else { return nil }
        let answering = Set(runs.filter { $0.noticeRun == true && !MightyGraphSupport.terminal($0.status) && originalRunID($0.id) == run.id }
            .flatMap(\.agents).compactMap(\.sourceBlockId))
        guard tasks.contains(where: { !MightyGraphSupport.terminal($0.status) || answering.contains($0.id) }) else { return nil }
        return Awaiting(tasks: tasks.map { AwaitedTask(id: $0.id, title: MightyGraphSupport.blockTitle($0), status: $0.status) })
    }
    /// The pane is alive only for the notices its newest request awaits (`awaitingNotice`): the pet, its status list and the
    /// pane header show it waiting, 알림 대기, instead of running. Its status stays running, so the completion notification
    /// still comes once, when the process ends.
    public static func awaitsNotice(_ pane: RunSession) -> Bool {
        guard pane.status == "running", let runs = pane.graphRuns, let newest = MightyGraphSupport.newestRequestRun(runs) else { return false }
        return awaitingNotice(newest, in: runs) != nil
    }
    /// The word for a pane that `awaitsNotice`.
    public static var stateWord: String { L("graph.state.awaitingNotice") }

    /// Agents a view draws for a run: background blocks only inside a notice run.
    /// Blocks under a hidden background block are hidden with it.
    public static func shownAgents(_ run: MightyGraphRun) -> [MightyGraphAgent] {
        guard run.noticeRun != true, run.agents.contains(where: { $0.background == true }) else { return run.agents }
        var hidden = Set(run.agents.filter { $0.background == true }.map(\.id))
        var grew = true
        while grew {
            grew = false
            for agent in run.agents where !hidden.contains(agent.id) {
                if let parent = agent.parentID, hidden.contains(parent) { hidden.insert(agent.id); grew = true }
            }
        }
        return run.agents.filter { !hidden.contains($0.id) }
    }
}
