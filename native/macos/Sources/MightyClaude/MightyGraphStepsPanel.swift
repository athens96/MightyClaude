import MightyCore
import SwiftUI

/// The agent's own step list over the diagram's top-left corner
/// (`ProgressSteps`, .omc/plans/progress-steps.md): a header with its count and
/// a fold button, then one row per step. Folded, only the header shows.
struct MightyGraphStepsPanel: View {
    let sessionID: String
    let panel: ProgressSteps.Panel
    /// The diagram area's height; the rows scroll within half of it.
    let areaHeight: CGFloat?
    @Binding var collapsed: Bool
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    /// About 240 points, from the layout tokens.
    static let width = DesignMetrics.Layout.sidebarMin + 2 * DesignMetrics.Spacing.xl

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            header
            if !collapsed {
                Divider().overlay(Palette.border)
                // A short list shows whole; a long one scrolls within half the diagram.
                ViewThatFits(in: .vertical) {
                    rows
                    ScrollView(.vertical) { rows }
                }
                .frame(maxHeight: areaHeight.map { $0 / 2 })
            }
        }
        .frame(width: Self.width, alignment: .leading)
        .background(Palette.panel, in: RoundedRectangle(cornerRadius: DesignMetrics.Spacing.md))
        .overlay { RoundedRectangle(cornerRadius: DesignMetrics.Spacing.md).stroke(Palette.border, lineWidth: 1) }
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("mighty-steps-\(sessionID)")
    }

    private var header: some View {
        HStack(spacing: DesignMetrics.Spacing.sm) {
            Image(systemName: "checklist").foregroundStyle(Palette.accent).accessibilityHidden(true)
            Text(L("graph.steps.title")).font(.ui(size: 12, weight: .semibold)).foregroundStyle(Palette.ink).lineLimit(1)
            Text(L("graph.steps.count", ["done": "\(panel.done)", "total": "\(panel.total)"]))
                .font(.ui(size: 10)).foregroundStyle(Palette.ink2).monospacedDigit()
            Spacer(minLength: DesignMetrics.Spacing.xs)
            Button { collapsed.toggle() } label: { Image(systemName: collapsed ? "chevron.right" : "chevron.down") }
                .buttonStyle(.plain).foregroundStyle(Palette.ink2)
                .frame(width: DesignMetrics.Layout.hitTarget, height: DesignMetrics.Layout.hitTarget).contentShape(Rectangle())
                .help(collapsed ? L("graph.steps.expand") : L("graph.steps.collapse"))
                .accessibilityLabel(collapsed ? L("graph.steps.expand") : L("graph.steps.collapse"))
                .accessibilityIdentifier("mighty-steps-toggle-\(sessionID)")
        }
        .padding(.horizontal, DesignMetrics.Spacing.md).padding(.vertical, DesignMetrics.Spacing.xs)
    }

    private var rows: some View {
        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xxs) {
            ForEach(Array(panel.rows.enumerated()), id: \.offset) { _, row in
                HStack(alignment: .firstTextBaseline, spacing: DesignMetrics.Spacing.sm) {
                    mark(row.state)
                    Text(row.text).font(.ui(size: 11)).foregroundStyle(row.state == .completed ? Palette.ink2 : Palette.ink)
                        .lineLimit(2).fixedSize(horizontal: false, vertical: true)
                        .frame(maxWidth: .infinity, alignment: .leading)
                }
                .accessibilityElement(children: .ignore)
                .accessibilityLabel(row.text + ", " + L(row.state.labelKey))
            }
        }
        .padding(.horizontal, DesignMetrics.Spacing.md).padding(.vertical, DesignMetrics.Spacing.sm)
    }

    @ViewBuilder private func mark(_ state: ProgressSteps.State) -> some View {
        switch state {
        case .completed:
            Image(systemName: "checkmark.circle.fill").font(.ui(size: 11)).foregroundStyle(Palette.done)
        case .inProgress:
            // It pulses only while the pane runs.
            Image(systemName: "circle.fill").font(.ui(size: 11)).foregroundStyle(Palette.accent)
                .symbolEffect(.pulse, options: .repeating, isActive: panel.running && !reduceMotion)
        case .pending:
            Image(systemName: "circle").font(.ui(size: 11)).foregroundStyle(Palette.ink2)
        }
    }
}
