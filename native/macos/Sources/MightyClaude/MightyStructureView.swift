import MightyCore
import SwiftUI

/// The Mighty graph drawn as its structure (design "A. 단계별 열"), from
/// `MightyStructure.make`: a styled pane's phases as a rail of chips with a column of
/// request cards under each, a free pane's requests as one 요청 → 실행 → 결과 row each.
/// Under every card its sub-agent trees sit in dashed boxes. A click on a card or a
/// block shows it in the diagram, as opening a timeline row aims the diagram's camera.
struct MightyStructureView: View {
    let sessionID: String
    let provider: String
    let shape: MightyStructure.Shape
    /// Request titles' style prefixes, as the diagram and the timeline title them.
    let styleTitles: StyleRequestTitles
    /// The newest request, kept in view as the timeline keeps it.
    let newestRunID: String?
    /// Shows a diagram node (`MightyGraphLayout.Node.id`) in the diagram.
    let onReveal: (String) -> Void

    /// A column, wide enough for a card's title line and its status pill.
    static let columnWidth: CGFloat = 240
    /// A tree's indent stops growing past this depth, so a deep chain stays inside its column.
    static let maximumIndent = 6
    private static let chipHeight: CGFloat = 20

    var body: some View {
        ScrollViewReader { proxy in
            ScrollView([.horizontal, .vertical]) {
                Group {
                    switch shape {
                    case .phases(let columns): phases(columns)
                    case .free(let rows): free(rows)
                    }
                }
                .padding(.horizontal, DesignMetrics.Inset.graphBlockBodyH).padding(.vertical, DesignMetrics.Spacing.md)
            }
            .defaultScrollAnchor(.bottomLeading)
            .onAppear { DispatchQueue.main.async { follow(proxy) } }
            // The newest card grows as its blocks arrive and settle; it stays in view, as
            // the timeline's newest request does.
            .onChange(of: newestGrowth) { _, _ in follow(proxy) }
        }
        .background(Palette.raised)
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("mighty-structure-\(sessionID)")
    }

    /// The newest request with what makes its card taller or settles it.
    private var newestGrowth: [String] {
        guard let newestRunID else { return [] }
        let item: MightyStructure.Item? = switch shape {
        case .phases(let columns): columns.lazy.flatMap(\.items).first { $0.runID == newestRunID }
        case .free(let rows): rows.last { $0.id == newestRunID }?.item
        }
        guard let item else { return [newestRunID] }
        return [newestRunID, item.status, item.result == nil ? "" : "result", "\(item.clusters.reduce(0) { $0 + $1.nodes.count })"]
    }

    private func follow(_ proxy: ScrollViewProxy) {
        guard let newestRunID else { return }
        if case .free = shape { proxy.scrollTo(newestRunID, anchor: .bottomLeading) } else { proxy.scrollTo(newestRunID, anchor: .bottom) }
    }

    // MARK: Styled pane

    private func phases(_ columns: [MightyStructure.Column]) -> some View {
        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.md) {
            rail(columns.map { ($0.id, $0.phase.title, $0.step) })
                .accessibilityElement(children: .contain)
                .accessibilityLabel(L("guidedPanel.phasesAccessibility"))
            HStack(alignment: .top, spacing: DesignMetrics.Spacing.lg) {
                ForEach(columns) { column in
                    LazyVStack(alignment: .leading, spacing: DesignMetrics.Spacing.md) {
                        ForEach(column.items) { card($0).id($0.id) }
                    }
                    .frame(width: Self.columnWidth, alignment: .topLeading)
                    .accessibilityElement(children: .contain)
                    .accessibilityLabel(stepAccessibility(column.phase.title, column.step))
                    .accessibilityIdentifier("mighty-structure-phase-\(column.id)")
                }
            }
        }
    }

    /// The chips over the columns, each joined to the next by a line that is green once
    /// the step before it is done.
    private func rail(_ steps: [(id: String, title: String, step: MightyStructure.Step)]) -> some View {
        HStack(alignment: .center, spacing: DesignMetrics.Spacing.lg) {
            ForEach(Array(steps.enumerated()), id: \.element.id) { index, item in
                HStack(spacing: 0) {
                    chip(item.title, step: item.step)
                    if index < steps.count - 1 {
                        Rectangle().fill(item.step == .done ? Palette.done : Palette.track)
                            .frame(height: 2).frame(maxWidth: .infinity)
                            .padding(.trailing, -DesignMetrics.Spacing.lg)
                            .accessibilityHidden(true)
                    } else {
                        Spacer(minLength: 0)
                    }
                }
                .frame(width: Self.columnWidth, alignment: .leading)
            }
        }
    }

    private func chip(_ title: String, step: MightyStructure.Step) -> some View {
        let look = Self.look(step)
        return HStack(spacing: DesignMetrics.Spacing.xs) {
            Image(systemName: look.icon).font(.system(size: 8, weight: .bold))
            Text(verbatim: title).font(.system(size: 11, weight: step == .current ? .bold : .semibold))
                .lineLimit(1).truncationMode(.tail)
        }
        .foregroundStyle(look.ink)
        .padding(.horizontal, DesignMetrics.Spacing.md).frame(height: Self.chipHeight)
        .background(look.fill, in: Capsule())
        // A long phase title is cut to its column; the whole title is in the help.
        .layoutPriority(1)
        .help(title)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(stepAccessibility(title, step))
    }

    /// done is green, current the accent, pending grey; an error or a stop in its own colour.
    private static func look(_ step: MightyStructure.Step) -> (fill: Color, ink: Color, icon: String) {
        switch step {
        case .done: (Palette.heroFill(.done), Palette.heroInk(.done), "checkmark")
        case .current: (Palette.accent, Palette.onAccent, "circle.fill")
        case .pending: (Palette.track, Palette.ink2, "circle")
        case .error: (Palette.heroFill(.err), Palette.heroInk(.err), "xmark")
        case .stopped: (Palette.heroFill(.stop), Palette.heroInk(.stop), "stop.fill")
        }
    }

    private func stepAccessibility(_ name: String, _ step: MightyStructure.Step) -> String {
        let state = switch step {
        case .done: L("graph.structure.step.done")
        case .current: L("graph.structure.step.current")
        case .pending: L("graph.structure.step.pending")
        case .error: L("graph.state.error")
        case .stopped: L("graph.state.stopped")
        }
        return L("graph.structure.stepAccessibility", ["name": name, "state": state])
    }

    // MARK: Free pane

    private func free(_ rows: [MightyStructure.Row]) -> some View {
        LazyVStack(alignment: .leading, spacing: DesignMetrics.Spacing.xl) {
            if rows.isEmpty {
                Text(L("graph.timeline.empty")).font(.system(size: 12)).foregroundStyle(Palette.ink2)
                    .padding(.vertical, DesignMetrics.Spacing.md)
            }
            ForEach(rows) { row in
                freeRow(row).id(row.id)
            }
        }
    }

    private func freeRow(_ row: MightyStructure.Row) -> some View {
        let item = row.item
        let steps: [(id: String, title: String, step: MightyStructure.Step)] = [
            ("request", L("graph.structure.column.request"), row.request),
            ("execution", L("graph.structure.column.execution"), row.execution),
            ("result", L("graph.structure.column.result"), row.result),
        ]
        return VStack(alignment: .leading, spacing: DesignMetrics.Spacing.sm) {
            Text(heading(item)).font(Palette.heading(13)).foregroundStyle(Palette.ink).lineLimit(1)
                .accessibilityAddTraits(.isHeader)
            rail(steps)
            HStack(alignment: .top, spacing: DesignMetrics.Spacing.lg) {
                // 요청
                revealButton(item.nodeID) {
                    Text(item.input.isEmpty ? " " : item.input).font(.system(size: 12)).foregroundStyle(Palette.ink).lineLimit(4)
                        .multilineTextAlignment(.leading)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(.horizontal, DesignMetrics.Inset.graphBlockBodyH).padding(.vertical, DesignMetrics.Spacing.sm)
                        .mightyBlockCard(cornerRadius: 10)
                }
                .frame(width: Self.columnWidth, alignment: .topLeading)
                .accessibilityValue(stepAccessibility(steps[0].title, row.request))
                // 실행
                VStack(alignment: .leading, spacing: DesignMetrics.Spacing.sm) {
                    revealButton(item.nodeID) {
                        HStack(spacing: DesignMetrics.Spacing.sm) {
                            MightyStatusPill(text: MightyGraphView.statusLabel(item.status), tone: DesignTone(status: item.status))
                            Text(L("graph.structure.tally", ["agents": "\(item.tally.agents)", "tools": "\(item.tally.tools)"]))
                                .font(.system(size: 11, weight: .semibold)).foregroundStyle(Palette.ink2).lineLimit(1)
                            Spacer(minLength: 0)
                        }
                        .padding(.horizontal, DesignMetrics.Inset.graphBlockBodyH).padding(.vertical, DesignMetrics.Spacing.sm)
                        .mightyBlockCard(cornerRadius: 10)
                    }
                    .accessibilityValue(stepAccessibility(steps[1].title, row.execution))
                    clusters(item.clusters)
                }
                .frame(width: Self.columnWidth, alignment: .topLeading)
                // 결과
                revealButton(MightyGraphBlockSize.nodeID(runID: item.runID, suffix: item.finished ? "result" : "request")) {
                    Text(item.result ?? (item.finished ? MightyGraphView.statusLabel(item.status) : L("graph.structure.resultPending")))
                        .font(.system(size: 12)).foregroundStyle(item.result == nil ? Palette.ink2 : Palette.ink).lineLimit(4)
                        .multilineTextAlignment(.leading)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(.horizontal, DesignMetrics.Inset.graphBlockBodyH).padding(.vertical, DesignMetrics.Spacing.sm)
                        .mightyBlockCard(cornerRadius: 10)
                }
                .frame(width: Self.columnWidth, alignment: .topLeading)
                .accessibilityValue(stepAccessibility(steps[2].title, row.result))
            }
        }
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("mighty-structure-request-\(item.runID)")
    }

    // MARK: Cards and clusters

    /// "Request N · Claude", its status, the request on two lines and, once it finished,
    /// its result on one.
    private func card(_ item: MightyStructure.Item) -> some View {
        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.sm) {
            revealButton(item.nodeID) {
                VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xs) {
                    HStack(spacing: DesignMetrics.Spacing.sm) {
                        Text(heading(item)).font(.system(size: 12, weight: .bold)).foregroundStyle(Palette.ink).lineLimit(1)
                        Spacer(minLength: DesignMetrics.Spacing.xs)
                        MightyStatusPill(text: MightyGraphView.statusLabel(item.status), tone: DesignTone(status: item.status))
                    }
                    if !item.input.isEmpty {
                        Text(item.input).font(.system(size: 11)).foregroundStyle(Palette.ink2).lineLimit(2)
                            .multilineTextAlignment(.leading)
                    }
                    if let result = item.result {
                        Label { Text(result).lineLimit(1) } icon: { Image(systemName: "text.badge.checkmark") }
                            .font(.system(size: 11)).foregroundStyle(Palette.text(item.status))
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(.horizontal, DesignMetrics.Inset.graphBlockBodyH).padding(.vertical, DesignMetrics.Spacing.sm)
                .mightyBlockCard(cornerRadius: 10)
            }
            .accessibilityIdentifier("mighty-structure-request-\(item.runID)")
            clusters(item.clusters)
        }
    }

    @ViewBuilder private func clusters(_ clusters: [MightyStructure.Cluster]) -> some View {
        if !clusters.isEmpty {
            VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xs) {
                ForEach(clusters) { cluster($0) }
            }
            .padding(.leading, DesignMetrics.Spacing.lg)
        }
    }

    private func cluster(_ cluster: MightyStructure.Cluster) -> some View {
        LazyVStack(alignment: .leading, spacing: 0) {
            ForEach(cluster.nodes) { node in
                revealButton(node.nodeID) { nodeRow(node) }
                    .accessibilityLabel(L("graph.structure.nodeAccessibility", ["title": node.title, "status": MightyGraphView.statusLabel(node.status)]))
                    .accessibilityIdentifier("mighty-structure-node-\(node.nodeID)")
            }
        }
        .padding(.horizontal, DesignMetrics.Spacing.sm).padding(.vertical, DesignMetrics.Spacing.xs)
        .overlay {
            RoundedRectangle(cornerRadius: 8, style: .continuous)
                .strokeBorder(Palette.border, style: StrokeStyle(lineWidth: 1, dash: [4, 3]))
                .allowsHitTesting(false)
        }
        .accessibilityElement(children: .contain)
        .accessibilityLabel(L("graph.structure.clusterAccessibility", ["count": "\(cluster.nodes.count)"]))
    }

    /// The block's status as a bar in its colour, its kind's glyph and ink as the
    /// diagram gives them (`MightyGraphView.agentPresentation`), and its title.
    private func nodeRow(_ node: MightyStructure.Node) -> some View {
        let task = node.kind == "task"
        return HStack(spacing: DesignMetrics.Spacing.sm) {
            RoundedRectangle(cornerRadius: 1.5).fill(Palette.mark(node.status)).frame(width: 3, height: 14)
            Image(systemName: task ? "terminal" : "person.crop.square.filled.and.at.rectangle")
                .font(.system(size: 9, weight: .semibold)).foregroundStyle(task ? Palette.taskText : Palette.agentText)
            Text(verbatim: node.title).font(.system(size: 11)).foregroundStyle(Palette.ink).lineLimit(1)
            Spacer(minLength: 0)
        }
        .padding(.leading, CGFloat(min(node.depth, Self.maximumIndent)) * DesignMetrics.Spacing.lg)
        .frame(minHeight: DesignMetrics.Layout.hitTarget)
    }

    private func revealButton<Content: View>(_ nodeID: String, @ViewBuilder label: () -> Content) -> some View {
        Button { onReveal(nodeID) } label: { label().contentShape(Rectangle()) }
            .buttonStyle(.plain)
            .help(L("graph.structure.reveal"))
            .accessibilityHint(L("graph.structure.reveal"))
    }

    private func heading(_ item: MightyStructure.Item) -> String {
        StyleChrome.requestTitle(prefix: styleTitles.prefix(item.input), ordinal: item.ordinal, providerLabel: ProviderOptions.label(provider))
    }
}
