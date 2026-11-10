import MightyCore
import SwiftUI

/// A sub-agent as the flow's compact card (`FlowCard`): its name, who it is, what it is doing right now, and its
/// state with the figures the app has. A click opens it: a finished background agent's card goes to its full copy
/// in the notice run, any other opens into its block in place. The running outline is the block's own.
struct MightyGraphAgentCard: View {
    let nodeID: String
    let content: FlowCard.Content
    /// The card's click: to the copy, or open in place.
    let onActivate: () -> Void
    /// What the click does, for the tooltip.
    let activateHelp: String
    /// The size button: always the block in place.
    let onOpen: () -> Void

    private static let corner: CGFloat = 10

    var body: some View {
        let ink = Palette.flow(content.ink)
        HStack(spacing: 0) {
            Rectangle().fill(ink).frame(width: DesignMetrics.Pipeline.cardBarWidth)
            VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xxs) {
                HStack(spacing: DesignMetrics.Spacing.xs) {
                    Text(verbatim: content.title).font(.ui(size: 12, weight: .bold)).foregroundStyle(Palette.ink).lineLimit(1).truncationMode(.tail)
                    if content.nested > 0 {
                        Text(verbatim: "(+\(content.nested))").font(.ui(size: 10, weight: .semibold)).foregroundStyle(Palette.ink2).fixedSize()
                            .help(L("graph.card.nested", ["count": "\(content.nested)"]))
                            .accessibilityLabel(L("graph.card.nested", ["count": "\(content.nested)"]))
                            .accessibilityIdentifier("mighty-card-nested-\(nodeID)")
                    }
                    Spacer(minLength: 0)
                    ForEach(content.marks, id: \.self) { mark in
                        Image(systemName: mark.symbol).font(.ui(size: 9, weight: .semibold)).foregroundStyle(Palette.ink2).fixedSize()
                            .help(mark.help).accessibilityLabel(mark.help)
                    }
                    Button(action: onOpen) { Image(systemName: "rectangle.expand.vertical") }
                        .buttonStyle(.plain).foregroundStyle(Palette.ink2)
                        .help(L("graph.card.open")).accessibilityLabel(L("graph.card.open"))
                        .accessibilityIdentifier("mighty-expand-\(nodeID)")
                }
                if let identity = content.identity {
                    Text(verbatim: identity).font(.ui(size: 10, weight: .semibold)).foregroundStyle(ink).lineLimit(1).truncationMode(.tail)
                        .accessibilityIdentifier("mighty-card-identity-\(nodeID)")
                }
                Text(verbatim: content.activity ?? " ").font(.code(size: 10.5)).foregroundStyle(Palette.ink2).lineLimit(1).truncationMode(.tail)
                    .accessibilityIdentifier("mighty-card-activity-\(nodeID)")
                Spacer(minLength: 0)
                FlowCardFigures(content: content).accessibilityIdentifier("mighty-card-figures-\(nodeID)")
            }
            .padding(.horizontal, DesignMetrics.Inset.graphBlockBodyH).padding(.vertical, DesignMetrics.Spacing.sm)
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .contentShape(Rectangle())
        .onTapGesture(perform: onActivate)
        .help(content.summary.isEmpty ? activateHelp : content.summary + "\n" + activateHelp)
        .mightyBlockCard(cornerRadius: Self.corner)
        .overlay { MightyGraphActivityOutline(tone: DesignTone(blockStatus: content.status), cornerRadius: Self.corner) }
        .accessibilityElement(children: .contain)
        .accessibilityAddTraits(.isButton)
        .accessibilityAction(named: Text(verbatim: activateHelp), onActivate)
        .accessibilityIdentifier("mighty-node-\(nodeID)")
    }
}

/// The card's last line: its state and its figures (`FlowCard.figures`). Only this line reads the clock, once a
/// second and only while the agent runs, so the time that passes redraws neither the card nor the diagram.
private struct FlowCardFigures: View {
    let content: FlowCard.Content

    var body: some View {
        if content.running, content.startedAt != nil {
            TimelineView(.periodic(from: .now, by: 1)) { context in line(now: context.date) }
        } else {
            line(now: Date())
        }
    }

    private func line(now: Date) -> some View {
        HStack(spacing: DesignMetrics.Spacing.xs) {
            Circle().fill(Palette.mark(content.status)).frame(width: 6, height: 6)
            Text(verbatim: FlowCard.figures(content, now: now)).font(.ui(size: 10).monospacedDigit())
                .foregroundStyle(Palette.text(DesignTone(blockStatus: content.status))).lineLimit(1).truncationMode(.tail)
        }
    }
}
