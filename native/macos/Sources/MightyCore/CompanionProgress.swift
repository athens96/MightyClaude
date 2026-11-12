import CoreGraphics
import Foundation

/// The pet's progress card, over its bubble (`CompanionProgressCard`): the progress panel of the agent the bubble shows, its
/// newest request (`FlowSteps.progress`), cut to what fits a small card. A few steps around the one in progress, then a few
/// pieces of background work; it folds to its heading. A request with neither steps nor background work has no card.
public struct CompanionProgress: Equatable, Sendable {
    public static let maximumRows = 5
    public static let maximumBackground = 3
    public static let headerHeight: CGFloat = 26
    public static let rowHeight: CGFloat = 20
    public static let sectionHeight: CGFloat = 18
    /// The card's padding under its last line and the gap to the bubble.
    public static let padding: CGFloat = 8
    public static let gap: CGFloat = 6

    /// "요청 27".
    public var heading: String
    /// "3/9", nil without steps.
    public var count: String?
    public var stateWord: String
    public var status: String
    public var running: Bool
    public var rows: [FlowSteps.Progress.Row]
    /// Steps the rows leave out.
    public var hidden: Int
    public var background: [FlowSteps.Background]
    public var backgroundHidden: Int
    /// "1/3": background work running of all of it.
    public var backgroundCount: String

    /// The card for `progress`; nil when its request has no steps and no background work.
    public static func make(_ progress: FlowSteps.Progress) -> CompanionProgress? {
        guard !progress.rows.isEmpty || !progress.background.isEmpty else { return nil }
        let rows = progress.rows
        let focus = rows.firstIndex { $0.state == .current } ?? rows.lastIndex { $0.state == .done } ?? 0
        let start = rows.count <= maximumRows ? 0 : max(0, min(focus - 1, rows.count - maximumRows))
        let shown = Array(rows[start..<min(rows.count, start + maximumRows)])
        let background = Array(progress.background.prefix(maximumBackground))
        let allBackground = progress.background.count + progress.backgroundHidden
        return CompanionProgress(heading: progress.heading, count: progress.count, stateWord: progress.stateWord, status: progress.status,
                                 running: progress.running, rows: shown, hidden: progress.hidden + rows.count - shown.count,
                                 background: background, backgroundHidden: allBackground - background.count,
                                 backgroundCount: "\(progress.backgroundRunning)/\(allBackground)")
    }

    /// The card of the newest request among a pane's runs (the runs as the pane draws them, `MightyGraphSupport.shownRuns`).
    public static func newest(in runs: [MightyGraphRun]) -> CompanionProgress? {
        guard let index = MightyGraphSupport.newestRequestIndex(runs) else { return nil }
        let ordinals = MightyGraphSupport.requestOrdinals(runs)
        return make(FlowSteps.progress(index, in: runs, heading: L("graph.timeline.requestOrdinal", ["n": "\(ordinals[index])"])))
    }

    /// How tall the card is, the gap to the bubble included: the window grows by this much.
    public func height(collapsed: Bool) -> CGFloat {
        var height = Self.headerHeight + Self.gap
        guard !collapsed else { return height }
        height += CGFloat(rows.count) * Self.rowHeight + (hidden > 0 ? Self.sectionHeight : 0)
        if !background.isEmpty {
            height += Self.sectionHeight + CGFloat(background.count) * Self.rowHeight + (backgroundHidden > 0 ? Self.sectionHeight : 0)
        }
        return height + Self.padding
    }
}
