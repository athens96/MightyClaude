import MightyCore
import SwiftUI

/// A sub-agent, or a command the session runs in the background, as the flow's compact card (`FlowCard`): its name,
/// who it is (a command: its command line, set as code), what it is doing right now, and its state with the figures the
/// app has. A click opens it: a finished background agent's or command's card goes to its full copy in the notice run,
/// any other opens into its block in place. The running outline is the block's own.
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
                    // A command's line is code, in the command's own quiet ink; an agent's is who it is, in its role's.
                    Text(verbatim: identity).font(content.isTask ? .code(size: 10) : .ui(size: 10, weight: .semibold))
                        .foregroundStyle(content.isTask ? Palette.ink2 : ink).lineLimit(1).truncationMode(.tail)
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

/// The line on the branch from a block to the sub-agents it delegated to (`FlowDelegation`): the count in ink, the
/// model in the sub-agent ink, how many still run in the run ink. It is a label, not a block: nothing in it is clicked.
struct MightyGraphDelegateLine: View {
    let nodeID: String
    let line: FlowDelegation.Line

    var body: some View {
        HStack(spacing: DesignMetrics.Spacing.xs) {
            Image(systemName: "arrow.turn.down.right").font(.ui(size: 9, weight: .semibold)).foregroundStyle(Palette.flow(.subagent))
            Text(verbatim: line.head).foregroundStyle(Palette.ink)
            // A row of sub-agents and commands: how many of each, in each one's ink.
            ForEach(Array(line.parts.enumerated()), id: \.offset) { _, part in
                Text(verbatim: "·").foregroundStyle(Palette.ink2)
                Text(verbatim: part.text).foregroundStyle(Palette.flow(part.role))
            }
            if let model = line.model {
                Text(verbatim: "·").foregroundStyle(Palette.ink2)
                Text(verbatim: model).foregroundStyle(Palette.flow(.subagent))
            }
            if let progress = line.progress {
                Text(verbatim: "·").foregroundStyle(Palette.ink2)
                Text(verbatim: progress).foregroundStyle(Palette.accent).monospacedDigit()
            }
        }
        .font(.ui(size: 10.5, weight: .semibold)).lineLimit(1).minimumScaleFactor(0.85)
        .padding(.horizontal, DesignMetrics.Spacing.md)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Palette.panel, in: Capsule())
        .overlay { Capsule().strokeBorder(Palette.border, lineWidth: 1).allowsHitTesting(false) }
        .help(line.text)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(line.text)
        .accessibilityIdentifier("mighty-node-\(nodeID)")
    }
}

/// The return node at a request's result position until the result is there (`FlowReturn`): the lead's ink, since
/// the work comes back to the main session, and the running outline, since it stands only while the run goes on.
struct MightyGraphReturnNode: View {
    let nodeID: String
    let state: FlowReturn.State

    private static let corner: CGFloat = 10

    var body: some View {
        let ink = Palette.flow(.lead)
        HStack(spacing: DesignMetrics.Spacing.sm) {
            Image(systemName: state.waiting ? "hourglass" : "arrow.uturn.backward").font(.ui(size: 10, weight: .semibold)).foregroundStyle(ink)
            Text(verbatim: state.text).font(.ui(size: 11, weight: .semibold)).foregroundStyle(ink).lineLimit(1).minimumScaleFactor(0.85)
        }
        .padding(.horizontal, DesignMetrics.Spacing.md)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .mightyBlockCard(cornerRadius: Self.corner)
        .overlay { MightyGraphActivityOutline(tone: .run, cornerRadius: Self.corner) }
        .help(state.text)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(state.text)
        .accessibilityIdentifier("mighty-node-\(nodeID)")
    }
}
