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
    /// Agents a view draws for a run: background blocks only inside a notice run.
    /// Blocks under a hidden background block are hidden with it.
    public static func shownAgents(_ run: MightyGraphRun) -> [MightyGraphAgent] {
        guard run.noticeRun != true else { return run.agents }
        var hidden = Set(run.agents.filter { $0.background == true }.map(\.id))
        guard !hidden.isEmpty else { return run.agents }
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
