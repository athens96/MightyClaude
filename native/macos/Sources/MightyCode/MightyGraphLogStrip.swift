import MightyCore
import SwiftUI

/// The flow's session log under the diagram (`FlowLog`): its header is the status line, who is doing what right now
/// with each part in its role's ink, and under it, while the strip is open, the last few events with their time, who
/// and what. Folded, only the header shows. A row leads to its block on the diagram.
struct MightyGraphLogStrip: View {
    let sessionID: String
    let status: FlowLog.Status
    /// The rows of an open strip, oldest first.
    let rows: [FlowLog.Row]
    let open: Bool
    /// The pane is too short for the rows beside the diagram (`FlowLog.rowsFit`): the status line alone, and no fold button —
    /// the pane's fold comes back with the room.
    var squeezed = false
    let onToggle: () -> Void
    let onReveal: (FlowLog.Row) -> Void

    /// One row's height. An open strip keeps room for `FlowLog.visibleRows` of them, so the diagram over it does not
    /// move as the rows come in.
    private static let rowHeight: CGFloat = 18
    /// The header line with the divider over it, and an open strip's rows with theirs: what `FlowLog.rowsFit` weighs.
    static let headerHeight: CGFloat = 1 + DesignMetrics.Layout.hitTarget
    static let rowsHeight: CGFloat = 1 + CGFloat(FlowLog.visibleRows) * rowHeight + 2 * DesignMetrics.Spacing.xxs
    /// The column of who did it: a model's name fits whole, a longer agent name is cut.
    private static let whoWidth: CGFloat = 132

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            Divider().overlay(Palette.border)
            header
            if open && !squeezed {
                Divider().overlay(Palette.border)
                list
            }
        }
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("mighty-log-\(sessionID)")
    }

    private var header: some View {
        HStack(spacing: DesignMetrics.Spacing.md) {
            Image(systemName: "list.bullet.rectangle").font(.ui(size: 11)).foregroundStyle(Palette.ink2).accessibilityHidden(true)
            Text(L("graph.log.title")).font(.ui(size: 11, weight: .semibold)).foregroundStyle(Palette.ink).lineLimit(1).fixedSize()
            Text(statusLine).font(.ui(size: 11)).lineLimit(1).truncationMode(.tail)
                .help(status.text)
                .accessibilityLabel(status.text)
                .accessibilityIdentifier("mighty-log-status-\(sessionID)")
            Spacer(minLength: DesignMetrics.Spacing.xs)
            // The strip stands at the pane's foot and opens upward.
            if !squeezed {
            Button(action: onToggle) { Image(systemName: open ? "chevron.down" : "chevron.up") }
                .buttonStyle(.plain).foregroundStyle(Palette.ink2)
                .frame(width: DesignMetrics.Layout.hitTarget, height: DesignMetrics.Layout.hitTarget).contentShape(Rectangle())
                .help(open ? L("graph.log.hide") : L("graph.log.show"))
                .accessibilityLabel(open ? L("graph.log.hide") : L("graph.log.show"))
                .accessibilityIdentifier("mighty-log-toggle-\(sessionID)")
            }
        }
        .padding(.horizontal, DesignMetrics.Inset.graphBarH)
    }

    /// The status with each part in its role's ink, as one line that is cut at its end.
    private var statusLine: AttributedString {
        var line = AttributedString()
        for (index, part) in status.parts.enumerated() {
            if index > 0 {
                var dot = AttributedString(" · ")
                dot.foregroundColor = Palette.ink2
                line += dot
            }
            var words = AttributedString(part.text)
            words.foregroundColor = Palette.flow(part.role)
            line += words
        }
        return line
    }

    private var list: some View {
        VStack(alignment: .leading, spacing: 0) {
            if rows.isEmpty {
                Text(L("graph.log.empty")).font(.ui(size: 11)).foregroundStyle(Palette.ink2).lineLimit(1)
                    .frame(height: Self.rowHeight)
                    .padding(.horizontal, DesignMetrics.Inset.graphBarH)
                    .accessibilityIdentifier("mighty-log-empty-\(sessionID)")
            }
            ForEach(rows) { row in
                Button { onReveal(row) } label: { line(row) }
                    .buttonStyle(.plain)
                    .help(row.what + "\n" + L("graph.log.reveal"))
                    .accessibilityElement(children: .ignore)
                    .accessibilityLabel([FlowLog.clock(row.time), row.who, row.what].joined(separator: ", "))
                    .accessibilityHint(L("graph.log.reveal"))
                    .accessibilityAddTraits(.isButton)
                    .accessibilityIdentifier("mighty-log-row-\(row.id)")
            }
        }
        .frame(maxWidth: .infinity, minHeight: CGFloat(FlowLog.visibleRows) * Self.rowHeight, alignment: .topLeading)
        .padding(.vertical, DesignMetrics.Spacing.xxs)
    }

    private func line(_ row: FlowLog.Row) -> some View {
        HStack(spacing: DesignMetrics.Spacing.md) {
            Text(verbatim: FlowLog.clock(row.time)).font(.ui(size: 10)).monospacedDigit().foregroundStyle(Palette.ink2).fixedSize()
            Text(verbatim: row.who).font(.ui(size: 11, weight: .semibold)).foregroundStyle(Palette.flow(row.ink)).lineLimit(1).truncationMode(.tail)
                .frame(width: Self.whoWidth, alignment: .leading)
            Text(verbatim: row.what).font(.ui(size: 11)).foregroundStyle(Palette.ink).lineLimit(1).truncationMode(.tail)
                .frame(maxWidth: .infinity, alignment: .leading)
        }
        .frame(height: Self.rowHeight)
        .padding(.horizontal, DesignMetrics.Inset.graphBarH)
        .contentShape(Rectangle())
    }
}
