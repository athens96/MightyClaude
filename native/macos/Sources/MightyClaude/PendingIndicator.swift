import AppKit
import MightyCore
import SwiftUI

extension PendingState {
    /// A failed action, whose message its own place shows.
    var isFailed: Bool { if case .failed = self { true } else { false } }
    /// Still waiting for its answer.
    var isPending: Bool { if case .pending = self { true } else { false } }
}

/// The moments a pending area may change (natural submit A1): when its loading may show,
/// when the action ended, and when the shortest showing is over. A `TimelineView` on it
/// draws again only then, never frame by frame.
struct PendingSchedule: TimelineSchedule {
    let dates: [Date]

    init(_ state: PendingState) {
        let show = DesignMetrics.Motion.seconds(DesignMetrics.Motion.pendingShowAfter)
        // The hold is inclusive (`PendingDisplay`): drawn again just after it ends.
        let hold = show + DesignMetrics.Motion.seconds(DesignMetrics.Motion.pendingMinVisible) + 0.001
        switch state {
        case .idle: dates = []
        case .pending(let since): dates = [since + show]
        case .done(let since, let at): dates = [since + show, at, since + hold].sorted()
        case .failed(_, let since, let at): dates = ([since + show] + (at.map { [$0] } ?? [])).sorted()
        }
    }

    func entries(from startDate: Date, mode: TimelineScheduleMode) -> [Date] {
        [startDate] + dates.filter { $0 > startDate }
    }
}

/// The shared motion of natural submit (`DesignMetrics.Motion`): the standard easing over a
/// token's duration, or none under Reduce Motion, which keeps every show and hide.
enum PendingMotion {
    static func animation(_ milliseconds: Int, reduceMotion: Bool) -> Animation? {
        guard !reduceMotion else { return nil }
        let curve = DesignMetrics.Motion.easing
        return .timingCurve(curve[0], curve[1], curve[2], curve[3], duration: DesignMetrics.Motion.seconds(milliseconds))
    }

    /// The system's Reduce Motion, for AppKit views that have no SwiftUI environment.
    @MainActor static var systemReducesMotion: Bool { NSWorkspace.shared.accessibilityDisplayShouldReduceMotion }
}

/// The diagram's activity bars (`MightyGraphActivityBars`), standing still at their resting
/// heights under Reduce Motion: it takes the movement away, not the mark.
struct PendingBars: View {
    var tint: Color = Palette.run
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        Group {
            if reduceMotion {
                HStack(spacing: DesignMetrics.Spacing.xxs) {
                    ForEach(0..<4, id: \.self) { index in
                        Capsule().fill(tint).frame(width: 3, height: MightyGraphActivityStyle.barHeight(index, phase: 0))
                    }
                }
            } else {
                MightyGraphActivityBars(tint: tint)
            }
        }
        .frame(width: 18, height: 14)
        .allowsHitTesting(false)
        .accessibilityHidden(true)
    }
}

/// One pending action's area (natural submit A3, the Mac's `PendingIndicator`): nothing until
/// `PendingDisplay` shows it, then the bars and `label` (전송 중 by default), and a failure's
/// message at once in place. New buttons and cards show their pending actions with it.
/// Named apart from MightyCore's `PendingIndicator`, the value it draws.
struct PendingIndicatorView: View {
    let state: PendingState
    var label: String = L("graph.state.sending")
    var showsLabel = true
    var tint: Color = Palette.run

    var body: some View {
        TimelineView(PendingSchedule(state)) { context in
            content(PendingDisplay.indicator(state, now: context.date))
        }
    }

    @ViewBuilder private func content(_ indicator: PendingIndicator) -> some View {
        switch indicator {
        case .hidden:
            EmptyView()
        case .loading:
            HStack(spacing: DesignMetrics.Spacing.xs) {
                PendingBars(tint: tint)
                if showsLabel { MightyStatusPill(text: label, tone: .run) }
            }
            .accessibilityElement(children: .ignore)
            .accessibilityLabel(label)
        case .failed(let message):
            Label(message, systemImage: "exclamationmark.circle")
                .font(.ui(size: 10.5)).foregroundStyle(Palette.errText).lineLimit(2)
        }
    }
}

/// Covers a control with the bars while its action is pending (`PendingDisplay`): the stop
/// button while the stop goes on.
private struct PendingOverlay: ViewModifier {
    let state: PendingState
    let tint: Color

    func body(content: Content) -> some View {
        TimelineView(PendingSchedule(state)) { context in
            let loading = PendingDisplay.indicator(state, now: context.date) == .loading
            content
                .opacity(loading ? 0 : 1)
                .overlay { if loading { PendingBars(tint: tint) } }
        }
    }
}

extension View {
    /// The control shows the pending bars in its place while `state` says so (natural submit A3).
    func pendingOverlay(_ state: PendingState, tint: Color = Palette.run) -> some View {
        modifier(PendingOverlay(state: state, tint: tint))
    }
}

/// A block's status mark: the activity bars and its status pill, as the diagram, the
/// structure and the timeline show them, or, while the request is sent and its run has not
/// answered (`pending`), 전송 중 by `PendingDisplay` and nothing before it shows.
struct MightyStatusMark: View {
    let status: String
    let text: String
    let tone: DesignTone
    /// The request's own pending state; nil for every other block.
    var pending: PendingState? = nil
    var showsActivity = true
    var pillHeight: CGFloat = 18
    /// A card's tinted edge around the pill (`tintedCardEdge`).
    var edge: DesignTone? = nil

    var body: some View {
        if let pending {
            TimelineView(PendingSchedule(pending)) { context in
                mark(PendingDisplay.indicator(pending, now: context.date), pendingNow: pending.isPending)
            }
        } else {
            mark(nil, pendingNow: false)
        }
    }

    @ViewBuilder private func mark(_ indicator: PendingIndicator?, pendingNow: Bool) -> some View {
        if indicator == .loading {
            PendingBars()
            MightyStatusPill(text: L("graph.state.sending"), tone: .run, height: pillHeight).tintedCardEdge(edge)
        } else if pendingNow {
            // Sent a moment ago: nothing yet, so a quick start never flashes 전송 중.
            EmptyView()
        } else {
            if showsActivity { MightyGraphActivityIndicator(status: status, tint: Palette.run) }
            MightyStatusPill(text: text, tone: tone, height: pillHeight).tintedCardEdge(edge)
        }
    }
}
