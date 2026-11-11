import MightyCore
import SwiftUI

/// The role view's progress panel (`FlowSteps.progress`), over the diagram's top-left corner where it stays while the diagram
/// scrolls under it: the request in view ("요청 27" and its first line) with its state, then its plan — every step with its
/// mark (done, the one in progress with its time running, not begun) and the time each took. The heading leads to the
/// request; a step that began leads to its card. The panel folds to its heading.
struct MightyGraphProgressPanel: View {
    let sessionID: String
    let progress: FlowSteps.Progress
    @Binding var collapsed: Bool
    let onRequest: () -> Void
    /// A row's click: the step's index among the request's steps.
    let onStep: (Int) -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: DesignMetrics.Spacing.sm) {
                Image(systemName: "list.bullet.clipboard").foregroundStyle(Palette.flow(.lead))
                Text(verbatim: progress.title).font(.ui(size: 12, weight: .bold)).foregroundStyle(Palette.ink)
                if let count = progress.count {
                    Text(verbatim: count).font(.ui(size: 11, weight: .semibold).monospacedDigit()).foregroundStyle(Palette.ink2)
                }
                Spacer(minLength: 0)
                Button { collapsed.toggle() } label: { Image(systemName: collapsed ? "chevron.down" : "chevron.up") }
                    .buttonStyle(.plain).foregroundStyle(Palette.ink2)
                    .help(L(collapsed ? "graph.steps.expand" : "graph.steps.collapse"))
                    .accessibilityLabel(L(collapsed ? "graph.steps.expand" : "graph.steps.collapse"))
                    .accessibilityIdentifier("mighty-progress-fold-\(sessionID)")
            }
            .padding(.horizontal, DesignMetrics.Inset.graphBlockBodyH)
            .frame(height: 30)
            Divider().overlay(Palette.border)
            // Which request: its number, its state and what it asked, in brief.
            Button(action: onRequest) {
                VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xxs) {
                    HStack(spacing: DesignMetrics.Spacing.xs) {
                        Text(verbatim: progress.heading).font(.ui(size: 11, weight: .bold)).foregroundStyle(Palette.ink)
                        MightyStatusMark(status: progress.status, text: progress.stateWord, tone: DesignTone(blockStatus: progress.status), showsActivity: progress.running)
                        Spacer(minLength: 0)
                    }
                    if !progress.request.isEmpty {
                        Text(verbatim: progress.request).font(.ui(size: 11)).foregroundStyle(Palette.ink2).lineLimit(2).truncationMode(.tail)
                            .multilineTextAlignment(.leading)
                    }
                }
                .padding(.horizontal, DesignMetrics.Inset.graphBlockBodyH).padding(.vertical, DesignMetrics.Spacing.sm)
                .frame(maxWidth: .infinity, alignment: .leading)
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .help(L("graph.progress.showRequest"))
            .accessibilityIdentifier("mighty-progress-request-\(sessionID)")
            if !collapsed, !progress.rows.isEmpty {
                Divider().overlay(Palette.border)
                VStack(alignment: .leading, spacing: 0) {
                    ForEach(progress.rows) { row in
                        Button { onStep(row.index) } label: { line(row) }
                            .buttonStyle(.plain)
                            .disabled(row.state == .pending)
                            .accessibilityIdentifier("mighty-progress-row-\(sessionID)-\(row.index)")
                    }
                    if progress.hidden > 0 {
                        Text(verbatim: L("graph.progress.more", ["count": "\(progress.hidden)"])).font(.ui(size: 10)).foregroundStyle(Palette.ink2)
                            .padding(.horizontal, DesignMetrics.Inset.graphBlockBodyH)
                            .frame(height: FlowSteps.progressRowHeight, alignment: .leading)
                    }
                }
                .padding(.vertical, DesignMetrics.Spacing.xs)
            }
        }
        .frame(width: FlowSteps.progressWidth, alignment: .topLeading)
        .mightyBlockCard()
        .shadow(color: .black.opacity(0.12), radius: 6, y: 2)
        .accessibilityElement(children: .contain)
        .accessibilityLabel(progress.title + " " + progress.heading)
        .accessibilityIdentifier("mighty-progress-\(sessionID)")
    }

    private func line(_ row: FlowSteps.Progress.Row) -> some View {
        HStack(spacing: DesignMetrics.Spacing.sm) {
            mark(row.state).frame(width: 14)
            Text(verbatim: row.text)
                .font(.ui(size: 11.5, weight: row.state == .current ? .semibold : .regular))
                .foregroundStyle(row.state == .pending ? Palette.ink2 : Palette.ink).opacity(row.state == .done ? 0.75 : 1)
                .lineLimit(1).truncationMode(.tail)
            Spacer(minLength: 0)
            ProgressTime(row: row, running: progress.running)
        }
        .padding(.horizontal, DesignMetrics.Inset.graphBlockBodyH)
        .frame(height: FlowSteps.progressRowHeight)
        .background(row.state == .current ? Palette.accent.opacity(0.10) : Color.clear)
        .contentShape(Rectangle())
        .help(row.text)
    }

    @ViewBuilder private func mark(_ state: FlowSteps.Progress.State) -> some View {
        switch state {
        case .done: Image(systemName: "checkmark.circle.fill").font(.ui(size: 11)).foregroundStyle(Palette.done)
        case .current: Image(systemName: "circle.inset.filled").font(.ui(size: 11)).foregroundStyle(Palette.run)
        case .pending: Image(systemName: "circle").font(.ui(size: 11)).foregroundStyle(Palette.ink2)
        case .stopped: Image(systemName: "stop.circle").font(.ui(size: 11)).foregroundStyle(Palette.stop)
        case .failed: Image(systemName: "exclamationmark.circle.fill").font(.ui(size: 11)).foregroundStyle(Palette.err)
        }
    }
}

/// A step's time: how long it took once it ended, the time since it began while it runs (read once a second), nothing for
/// a step not begun.
private struct ProgressTime: View {
    let row: FlowSteps.Progress.Row
    let running: Bool

    var body: some View {
        if let start = row.startedAt {
            if row.state == .current, running, row.endedAt == nil {
                TimelineView(.periodic(from: .now, by: 1)) { context in text(StyleWidgetPresentation.elapsed(from: start, to: context.date)) }
            } else if let end = row.endedAt, end >= start {
                text(StyleWidgetPresentation.elapsed(from: start, to: end))
            }
        }
    }

    private func text(_ value: String) -> some View {
        Text(verbatim: value).font(.ui(size: 10).monospacedDigit()).foregroundStyle(Palette.ink2).lineLimit(1).fixedSize()
    }
}
