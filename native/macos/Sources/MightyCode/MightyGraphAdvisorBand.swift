import MightyCore
import SwiftUI

/// The advisor's band beside a run (`FlowAdvisor`): its header names the advisor, its model and what it is doing;
/// under it one row for each consultation (who asked, when, where in the caller's work, how it ended), then, when a
/// reply had no text to show, the line that says so, and the totals. The advisor's ink; a row leads to the block that
/// asked. Nothing in it is an estimate, and no advice is shown that the CLI did not give as text.
struct MightyGraphAdvisorBand: View {
    let nodeID: String
    let band: FlowAdvisor.Band
    /// The band's laid-out height, which decides how many rows it has room for (`FlowAdvisor.visible`).
    let height: CGFloat
    /// A row's click: the camera goes to the block that asked.
    let onReveal: (String) -> Void

    private static let corner: CGFloat = 10

    var body: some View {
        let ink = Palette.flow(.advisor)
        let shown = FlowAdvisor.visible(band, height: height)
        HStack(spacing: 0) {
            Rectangle().fill(ink).frame(width: DesignMetrics.Pipeline.cardBarWidth)
            VStack(alignment: .leading, spacing: 0) {
                header(ink)
                Divider().overlay(Palette.border)
                if band.calls == 0 {
                    Text(L("graph.advisor.empty")).font(.ui(size: 10.5)).foregroundStyle(Palette.ink2).lineLimit(1)
                        .padding(.horizontal, DesignMetrics.Inset.graphBlockBodyH)
                        .frame(height: FlowAdvisor.rowHeight, alignment: .leading)
                        .accessibilityIdentifier("mighty-advisor-empty-\(nodeID)")
                }
                if shown.earlier > 0 {
                    Text(L("graph.advisor.earlier", ["count": "\(shown.earlier)"])).font(.ui(size: 10)).foregroundStyle(Palette.ink2).lineLimit(1)
                        .padding(.horizontal, DesignMetrics.Inset.graphBlockBodyH)
                        .frame(height: FlowAdvisor.earlierHeight, alignment: .leading)
                        .accessibilityIdentifier("mighty-advisor-earlier-\(nodeID)")
                }
                ForEach(shown.rows) { row in self.row(row) }
                Spacer(minLength: 0)
                if let note = band.note {
                    Text(note).font(.ui(size: 10)).foregroundStyle(Palette.ink2).lineLimit(2).fixedSize(horizontal: false, vertical: true)
                        .padding(.horizontal, DesignMetrics.Inset.graphBlockBodyH)
                        .frame(height: FlowAdvisor.noteHeight, alignment: .leading)
                        .accessibilityIdentifier("mighty-advisor-note-\(nodeID)")
                }
                Divider().overlay(Palette.border)
                Text(verbatim: band.totals).font(.ui(size: 10, weight: .semibold).monospacedDigit()).foregroundStyle(Palette.ink2).lineLimit(1)
                    .padding(.horizontal, DesignMetrics.Inset.graphBlockBodyH)
                    .frame(height: FlowAdvisor.footerHeight - 1, alignment: .leading)
                    .help(band.usage?.detail ?? band.totals)
                    .accessibilityIdentifier("mighty-advisor-totals-\(nodeID)")
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .mightyBlockCard(cornerRadius: Self.corner)
        // At work on a consultation, the band has the running outline the blocks have.
        .overlay { if band.state == .consulting { MightyGraphActivityOutline(tone: .run, cornerRadius: Self.corner) } }
        .accessibilityElement(children: .contain)
        .accessibilityLabel(band.header + ", " + band.totals)
        .accessibilityIdentifier("mighty-node-\(nodeID)")
    }

    private func header(_ ink: Color) -> some View {
        HStack(spacing: DesignMetrics.Spacing.xs) {
            Image(systemName: "lightbulb").font(.ui(size: 11, weight: .semibold)).foregroundStyle(ink).accessibilityHidden(true)
            Text(verbatim: L("graph.identity.role.advisor")).font(.ui(size: 12, weight: .bold)).foregroundStyle(Palette.ink).lineLimit(1).fixedSize()
            if let model = band.model {
                Text(verbatim: model).font(.ui(size: 11, weight: .semibold)).foregroundStyle(ink).lineLimit(1).truncationMode(.tail)
            }
            Spacer(minLength: DesignMetrics.Spacing.xs)
            if let state = band.state {
                Text(verbatim: FlowAdvisor.words(state)).font(.ui(size: 10, weight: .semibold))
                    .foregroundStyle(state == .consulting ? Palette.accent : Palette.ink2).lineLimit(1).fixedSize()
            }
        }
        .padding(.horizontal, DesignMetrics.Inset.graphBlockBodyH)
        .frame(height: FlowAdvisor.headerHeight - 1)
        .help(band.header)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(band.header)
        .accessibilityIdentifier("mighty-advisor-header-\(nodeID)")
    }

    private func row(_ row: FlowAdvisor.Row) -> some View {
        Button { onReveal(row.callerNodeID) } label: {
            VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xxs) {
                HStack(spacing: DesignMetrics.Spacing.xs) {
                    Text(verbatim: row.caller).font(.ui(size: 11, weight: .semibold)).foregroundStyle(Palette.flow(row.ink)).lineLimit(1).truncationMode(.tail)
                    Spacer(minLength: DesignMetrics.Spacing.xs)
                    Text(verbatim: Self.when(row)).font(.ui(size: 10).monospacedDigit()).foregroundStyle(Palette.ink2).lineLimit(1).fixedSize()
                }
                Text(verbatim: row.line).font(.ui(size: 10))
                    .foregroundStyle(row.state == "error" ? Palette.errText : row.state == "consulting" ? Palette.accent : Palette.ink2)
                    .lineLimit(1).truncationMode(.tail)
            }
            .padding(.horizontal, DesignMetrics.Inset.graphBlockBodyH)
            .frame(maxWidth: .infinity, alignment: .leading)
            .frame(height: FlowAdvisor.rowHeight)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        // The advice itself only when the CLI gave it as text.
        .help(([row.caller + " · " + row.line] + [row.text].compactMap { $0 } + [L("graph.advisor.reveal")]).joined(separator: "\n"))
        .accessibilityElement(children: .ignore)
        .accessibilityLabel([row.caller, Self.when(row), row.line].filter { !$0.isEmpty }.joined(separator: ", "))
        .accessibilityHint(L("graph.advisor.reveal"))
        .accessibilityAddTraits(.isButton)
        .accessibilityIdentifier("mighty-advisor-row-\(row.id)")
    }

    /// "18:16:51 · 8초": when it was asked and how long the reply took.
    private static func when(_ row: FlowAdvisor.Row) -> String {
        guard let started = row.started else { return "" }
        let clock = FlowLog.clock(started)
        guard let seconds = row.seconds else { return clock }
        return clock + " · " + StyleWidgetPresentation.elapsed(from: started, to: started.addingTimeInterval(seconds))
    }
}
