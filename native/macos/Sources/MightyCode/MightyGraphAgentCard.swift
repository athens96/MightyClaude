import MightyCore
import SwiftUI

/// A block of the role view folded to its card (`FlowCard`): a sub-agent, a command the session runs in the background,
/// the 실행 block or a step (`FlowSteps`). It wears the full block's frame — the same header with its glyph, title, status
/// pill and size button, the glyph in the role's ink, the same corners — and holds two lines under it:
/// what it is doing (or did) right now, and who it is with its figures. A click opens it: a finished background agent's or
/// command's card goes to its full copy in the notice run, any other opens into its block in place.
struct MightyGraphAgentCard: View {
    let nodeID: String
    let content: FlowCard.Content
    /// The glyph its full block wears.
    let icon: String
    /// The card's click: to the copy, or open in place.
    let onActivate: () -> Void
    /// What the click does, for the tooltip.
    let activateHelp: String
    /// The size button: always the block in place.
    let onOpen: () -> Void

    var body: some View {
        let ink = Palette.flow(content.ink)
        VStack(spacing: 0) {
            HStack(spacing: DesignMetrics.Spacing.sm) {
                Image(systemName: icon).foregroundStyle(ink)
                Text(verbatim: content.title).font(.ui(size: 12, weight: .bold)).foregroundStyle(Palette.ink).lineLimit(1).truncationMode(.tail)
                if content.nested > 0 {
                    Text(verbatim: "(+\(content.nested))").font(.ui(size: 10, weight: .semibold)).foregroundStyle(Palette.ink2).fixedSize()
                        .help(L("graph.card.nested", ["count": "\(content.nested)"]))
                        .accessibilityLabel(L("graph.card.nested", ["count": "\(content.nested)"]))
                        .accessibilityIdentifier("mighty-card-nested-\(nodeID)")
                }
                ForEach(content.marks, id: \.self) { mark in
                    Image(systemName: mark.symbol).font(.ui(size: 9, weight: .semibold)).foregroundStyle(Palette.ink2).fixedSize()
                        .help(mark.help).accessibilityLabel(mark.help)
                }
                Spacer(minLength: 3)
                MightyStatusMark(status: content.isPending ? "completed" : content.status, text: content.stateWord, tone: content.tone,
                                 showsActivity: !content.isPending)
                Button(action: onOpen) { Image(systemName: "rectangle.expand.vertical") }
                    .buttonStyle(.plain).foregroundStyle(Palette.ink2)
                    .help(L("graph.card.open")).accessibilityLabel(L("graph.card.open"))
                    .accessibilityIdentifier("mighty-expand-\(nodeID)")
            }
            .padding(.horizontal, DesignMetrics.Inset.graphBlockBodyH).frame(height: DesignMetrics.Layout.blockHead)
            Divider().overlay(Palette.border)
            VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xxs) {
                // The step of the request's plan it was launched in: what it is part of.
                if let step = content.step {
                    Label { Text(verbatim: step).lineLimit(1).truncationMode(.tail) } icon: { Image(systemName: "arrow.turn.down.right") }
                        .font(.ui(size: 10.5, weight: .semibold)).foregroundStyle(Palette.flow(.lead))
                        .help(L("graph.card.inStep", ["step": step]))
                        .accessibilityLabel(L("graph.card.inStep", ["step": step]))
                        .accessibilityIdentifier("mighty-card-step-\(nodeID)")
                }
                // What the main session said reads as text in the card's own ink; a command is code, quieter.
                Text(verbatim: content.activity ?? (content.isPending ? content.stateWord : " "))
                    .font(content.proseActivity || content.activity == nil ? .ui(size: 11.5) : .code(size: 10.5))
                    .foregroundStyle(content.proseActivity ? Palette.ink : Palette.ink2).lineLimit(1).truncationMode(.tail)
                    .accessibilityIdentifier("mighty-card-activity-\(nodeID)")
                FlowCardFigures(nodeID: nodeID, content: content, identity: content.identity)
            }
            .padding(.horizontal, DesignMetrics.Inset.graphBlockBodyH).padding(.vertical, DesignMetrics.Spacing.sm)
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .contentShape(Rectangle())
        .onTapGesture(perform: onActivate)
        .help(content.summary.isEmpty ? activateHelp : content.summary + "\n" + activateHelp)
        // A step not begun is quieter than the rest, and nothing on it moves.
        .opacity(content.isPending ? 0.6 : 1)
        .mightyBlockCard()
        .overlay { MightyGraphActivityOutline(tone: content.tone, cornerRadius: 12) }
        .accessibilityElement(children: .contain)
        .accessibilityAddTraits(.isButton)
        .accessibilityAction(named: Text(verbatim: activateHelp), onActivate)
        .accessibilityIdentifier("mighty-node-\(nodeID)")
    }
}

private extension FlowCard.Content {
    /// The card's tone: its state's, idle for a step not begun.
    var tone: DesignTone { isPending ? .idle : DesignTone(blockStatus: status) }
}

/// The card's second line: who it is (a command: its command line, set as code), then its figures — a command's exit
/// code, the time, the tools and the tokens (`FlowCard.figureParts`); the state is the header's pill. Only this line reads
/// the clock, once a second and only while it runs, so the time that passes redraws neither the card nor the diagram.
private struct FlowCardFigures: View {
    let nodeID: String
    let content: FlowCard.Content
    let identity: String?

    var body: some View {
        if content.running, content.startedAt != nil {
            TimelineView(.periodic(from: .now, by: 1)) { context in line(now: context.date) }
        } else {
            line(now: Date())
        }
    }

    private func line(now: Date) -> some View {
        let parts = FlowCard.figureParts(content)
        let figures = (parts.before.dropFirst() + [FlowCard.elapsed(content, now: now)].compactMap { $0 } + parts.after).joined(separator: " · ")
        return HStack(spacing: DesignMetrics.Spacing.xs) {
            if let identity {
                Text(verbatim: identity).font(content.isTask ? .code(size: 10) : .ui(size: 10, weight: .semibold))
                    .foregroundStyle(content.isTask ? Palette.ink2 : Palette.flow(content.ink)).lineLimit(1).truncationMode(.tail)
                    .accessibilityIdentifier("mighty-card-identity-\(nodeID)")
            }
            if identity != nil, !figures.isEmpty { Text(verbatim: "·").font(.ui(size: 10)).foregroundStyle(Palette.ink2) }
            if !figures.isEmpty {
                Text(verbatim: figures).font(.ui(size: 10).monospacedDigit()).foregroundStyle(Palette.ink2).lineLimit(1).fixedSize()
            }
            Spacer(minLength: 0)
        }
        .accessibilityElement(children: .combine)
        .accessibilityIdentifier("mighty-card-figures-\(nodeID)")
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
