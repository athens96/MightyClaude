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
    public static func heading(_ run: MightyGraphRun) -> String {
        guard let title = run.noticeTitle?.trimmingCharacters(in: .whitespacesAndNewlines), !title.isEmpty else { return genericTitle }
        return title
    }
    /// The symbol in front of a notice run's heading: the task block's (`MightyGraphView.agentPresentation`).
    public static let icon = "terminal"
    /// The 실행 block's title: "실행 · 요청 n", or plain "실행" in a notice run, which is no request.
    public static func executionTitle(notice: Bool, ordinal: Int) -> String {
        notice ? L("graph.block.execution") : L("graph.block.executionOf", ["n": "\(ordinal)"])
    }
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
