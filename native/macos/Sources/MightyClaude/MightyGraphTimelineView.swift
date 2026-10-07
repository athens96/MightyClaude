import MightyCore
import SwiftUI

/// The pieces of the Mighty timeline (concept D, frame 3 B). The rows come from
/// `MightyTimeline.groups`, which reads the same runs the diagram lays out; the list
/// itself, its clicks and its loading are put together in `MightyGraphView`.

/// The "다이어그램 | 타임라인" switch in the Mighty bar: a light segmented control on
/// the card, the chosen side a white chip on the track.
struct MightyViewSwitch: View {
    let sessionID: String
    let mode: MightyGraphViewMode
    let onSelect: (MightyGraphViewMode) -> Void

    var body: some View {
        HStack(spacing: 2) {
            option(.diagram, title: L("graph.view.diagram"), symbol: "point.3.connected.trianglepath.dotted")
            option(.timeline, title: L("graph.view.timeline"), symbol: "list.bullet.indent")
        }
        .padding(2)
        .background(Palette.track, in: RoundedRectangle(cornerRadius: 8, style: .continuous))
        .fixedSize()
        .accessibilityElement(children: .contain)
        .accessibilityLabel(L("graph.view.switch"))
    }

    private func option(_ value: MightyGraphViewMode, title: String, symbol: String) -> some View {
        let selected = mode == value
        let shape = RoundedRectangle(cornerRadius: 6, style: .continuous)
        return Button { onSelect(value) } label: {
            Label(title, systemImage: symbol)
                .font(.system(size: 11, weight: selected ? .bold : .semibold)).lineLimit(1)
                .foregroundStyle(selected ? Palette.ink : Palette.ink2)
                .padding(.horizontal, DesignMetrics.Spacing.md).frame(height: DesignMetrics.Layout.hitTarget)
                .background(selected ? Palette.panel : Color.clear, in: shape)
                .contentShape(shape)
        }
        .buttonStyle(.plain)
        .accessibilityAddTraits(selected ? .isSelected : [])
        .accessibilityIdentifier("mighty-view-\(value.rawValue)-\(sessionID)")
    }
}

/// Words the timeline puts beside a block.
enum MightyTimelineText {
    static func kind(_ kind: String) -> String {
        switch kind {
        case "main": L("graph.timeline.kind.request")
        case "task": L("graph.block.task")
        case "steer": L("graph.block.steer")
        case "compact": L("graph.block.compact")
        case "question": L("graph.block.question")
        default: L("graph.block.agent")
        }
    }

    /// The span of a settled block's records, worded as a tool's duration is.
    static func duration(_ milliseconds: Double?) -> String? {
        guard let milliseconds, milliseconds.isFinite, milliseconds >= 0 else { return nil }
        if milliseconds < 1_000 { return "\(Int(milliseconds))ms" }
        if milliseconds < 60_000 { return L("run.activity.durationSeconds", ["seconds": String(format: "%.1f", floor(milliseconds / 100) / 10)]) }
        let seconds = Int(milliseconds / 1_000)
        return seconds % 60 == 0 ? L("run.activity.durationMinutes", ["minutes": "\(seconds / 60)"])
            : L("run.activity.durationMinutesSeconds", ["minutes": "\(seconds / 60)", "seconds": "\(seconds % 60)"])
    }
}

/// The rail and node beside one row. Drawn as the row's background so it is as tall as
/// the row; the rail under the node runs on through the gap to the next row.
struct MightyTimelineMarker: View {
    let node: MightyTimeline.Node
    /// nil: the first row, with no rail above it.
    let above: DesignTone??
    let last: Bool
    let icon: String
    var gap = rowGap

    /// The space between a request's rows, which the rail runs on through.
    static let rowGap = DesignMetrics.Spacing.xs
    static let width: CGFloat = 32
    static let nodeSize: CGFloat = 24
    /// Two points under the row card's own top padding.
    static let nodeTop = DesignMetrics.Inset.graphBlockBodyV + 2
    static var centre: CGFloat { nodeTop + nodeSize / 2 }

    var body: some View {
        ZStack(alignment: .top) {
            if let above {
                rail(above).frame(width: 3, height: Self.centre)
                    .frame(maxHeight: .infinity, alignment: .top)
            }
            if !last {
                rail(node.rail).frame(width: 3)
                    .padding(.top, Self.centre).padding(.bottom, -gap)
            }
            ZStack {
                if node.ring { MightyTimelineRing(color: Palette.heroFill(node.tone)) }
                Circle().fill(Palette.heroFill(node.tone))
                    .overlay { Circle().strokeBorder(Palette.raised, lineWidth: 3) }
                Image(systemName: icon).font(.system(size: 9, weight: .bold))
                    .foregroundStyle(Palette.heroInk(node.tone))
            }
            .frame(width: Self.nodeSize, height: Self.nodeSize)
            .padding(.top, Self.nodeTop)
        }
        .frame(width: Self.width)
        .frame(maxHeight: .infinity, alignment: .top)
        .allowsHitTesting(false)
        .accessibilityHidden(true)
    }

    private func rail(_ tone: DesignTone?) -> some View {
        Rectangle().fill(tone.map(Palette.heroFill) ?? Palette.track)
    }
}

/// The running node's halo: a disc in its colour that grows and fades, over and over.
/// Under Reduce Motion it holds still as two soft rings.
struct MightyTimelineRing: View {
    let color: Color
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @ViewState private var spread = false

    var body: some View {
        Group {
            if reduceMotion {
                Circle().fill(color.opacity(0.10)).frame(width: 42, height: 42)
                    .overlay { Circle().fill(color.opacity(0.28)).frame(width: 32, height: 32) }
            } else {
                Circle().fill(color)
                    .scaleEffect(spread ? 1.95 : 1)
                    .opacity(spread ? 0 : 0.55)
                    .onAppear {
                        withAnimation(.easeOut(duration: 1.6).repeatForever(autoreverses: false)) { spread = true }
                    }
            }
        }
        .allowsHitTesting(false)
    }
}

/// One block on the timeline: a white card with its title, "kind · meta", the latest
/// thing it did while it runs, and its status pill. Clicking it opens what the
/// diagram's card holds.
struct MightyTimelineRowCard<Detail: View>: View {
    let row: MightyTimeline.Row
    /// The block's title as the Mac words it (the request's own block is localized).
    let title: String
    let icon: String
    let tint: Color
    let meta: [String]
    let status: String
    let open: Bool
    let onToggle: () -> Void
    @ViewBuilder var detail: () -> Detail

    var body: some View {
        let shape = RoundedRectangle(cornerRadius: 14, style: .continuous)
        let tone = row.node.tone
        VStack(alignment: .leading, spacing: 0) {
            Button(action: onToggle) {
                HStack(alignment: .top, spacing: DesignMetrics.Spacing.md) {
                    VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xxs) {
                        Text(title).font(.system(size: 13, weight: .bold)).foregroundStyle(Palette.ink)
                            .lineLimit(2).multilineTextAlignment(.leading)
                        HStack(spacing: 4) {
                            Image(systemName: icon).font(.system(size: 9, weight: .bold)).foregroundStyle(tint)
                            Text(([MightyTimelineText.kind(row.block.kind)] + meta).joined(separator: " · "))
                                .lineLimit(1).truncationMode(.tail)
                        }
                        .font(.system(size: 11)).foregroundStyle(Palette.ink2)
                        if let latest = row.latest {
                            HStack(spacing: 6) {
                                if tone == .run { PulseDot() }
                                Text(latest).font(.system(size: 10.8, design: .monospaced)).lineLimit(1).truncationMode(.tail)
                            }
                            .foregroundStyle(Palette.ink2).padding(.top, 2)
                        }
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                    MightyStatusPill(text: status, tone: tone, height: 19)
                }
                .padding(.horizontal, DesignMetrics.Inset.graphBlockBodyH).padding(.vertical, DesignMetrics.Inset.graphBlockBodyV)
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .help(open ? L("phone.blocks.collapse") : L("phone.blocks.expand"))
            .accessibilityLabel(title + ", " + status)
            .accessibilityHint(open ? L("phone.blocks.collapse") : L("phone.blocks.expand"))
            .accessibilityIdentifier("mighty-timeline-row-\(row.nodeID)")
            if open {
                Divider().overlay(Palette.border)
                detail()
            }
        }
        .background(Palette.panel, in: shape)
        .clipShape(shape)
        .overlay {
            switch tone {
            case .run: shape.strokeBorder(Palette.run, lineWidth: 2)
            case .wait: shape.strokeBorder(Palette.wait, lineWidth: 2)
            default: shape.strokeBorder(Palette.border, lineWidth: 1)
            }
        }
        .accessibilityElement(children: .contain)
    }
}

/// The card under a finished request: its header strip in the outcome's colour (green
/// once it finished well), and the final answer, opened in full on request.
struct MightyTimelineResultCard<Files: View>: View {
    let result: MightyTimeline.Result
    let title: String
    let caption: String
    let full: Bool
    let onToggleFull: () -> Void
    @ViewBuilder var files: () -> Files

    static var previewLines: Int { 8 }

    var body: some View {
        let shape = RoundedRectangle(cornerRadius: 16, style: .continuous)
        let ink = Palette.heroInk(result.tone)
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: DesignMetrics.Spacing.sm) {
                Image(systemName: Self.icon(result.tone)).font(.system(size: 11, weight: .bold))
                Text(title).font(.system(size: 12, weight: .heavy)).lineLimit(1)
                Spacer(minLength: 6)
                files()
                Text(caption).font(.system(size: 10.5, weight: .semibold, design: .monospaced)).lineLimit(1)
            }
            .foregroundStyle(ink)
            .padding(.horizontal, DesignMetrics.Inset.graphBlockBodyH).padding(.vertical, DesignMetrics.Inset.graphBlockBodyV)
            .background(Palette.heroFill(result.tone))
            if let text = result.text {
                VStack(alignment: .leading, spacing: 4) {
                    Text(text).font(.system(size: 12.5)).foregroundStyle(Palette.ink)
                        .lineLimit(full ? nil : Self.previewLines)
                        .textSelection(.enabled)
                        .frame(maxWidth: .infinity, alignment: .leading)
                    if Self.folds(text) {
                        Button(action: onToggleFull) {
                            Text(full ? L("phone.blocks.resultLess") : L("phone.blocks.resultMore"))
                                .font(.system(size: 12, weight: .bold)).foregroundStyle(Palette.accent)
                        }
                        .buttonStyle(.plain)
                        .accessibilityIdentifier("mighty-timeline-result-more-\(result.nodeID)")
                    }
                }
                .padding(.horizontal, DesignMetrics.Inset.graphBlockBodyH).padding(.top, DesignMetrics.Spacing.sm).padding(.bottom, DesignMetrics.Spacing.md)
            }
        }
        .background(Palette.panel, in: shape)
        .clipShape(shape)
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("mighty-timeline-result-\(result.nodeID)")
    }

    /// Whether the folded card could be leaving lines out: a cheap guess from the text
    /// itself, so the "more" link is offered only where there can be more.
    static func folds(_ text: String) -> Bool {
        text.count > 600 || text.split(separator: "\n", omittingEmptySubsequences: false).count > previewLines
    }

    static func icon(_ tone: DesignTone) -> String {
        switch tone {
        case .err: "exclamationmark.triangle.fill"
        case .stop: "stop.circle.fill"
        default: "checkmark.seal.fill"
        }
    }
}
