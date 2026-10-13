import AppKit
import MightyCore
import SwiftUI

/// What the structure view keeps of a pane while the pane shows its diagram or timeline,
/// so coming back finds the same cards open. Held by the pane's `MightyGraphView`.
final class MightyStructureMemory: ObservableObject {
    /// Texts opened past their folded lines, by `MightyStructureView.foldKey`.
    @Published var expanded = Set<String>()
    /// The view is at or near its bottom (`TranscriptViewport.isFollowing`), so the
    /// newest card's growth may scroll it. Not published: scrolling redraws nothing.
    var following = true
}

/// The Mighty graph drawn as its structure (design "A. 단계별 열"), from
/// `MightyStructure.make`: a styled pane's phases as a rail of chips with a column of
/// request cards under each, a free pane's requests as one 요청 → 실행 → 결과 row each.
/// Under every card its sub-agent trees sit in dashed boxes.
///
/// The cards read on their own: the request alone, then its 실행 part with the latest
/// reply, the last tool calls and the status, the answer once it finished, and each
/// sub-agent's prompt, output and last tool call. A long text opens in place; the small
/// button in a card's header shows its block in the diagram, as opening a timeline row
/// aims the diagram's camera.
struct MightyStructureView: View {
    let sessionID: String
    let provider: String
    let shape: MightyStructure.Shape
    /// Request titles' style prefixes, as the diagram and the timeline title them.
    let styleTitles: StyleRequestTitles
    /// The newest request, kept in view as the timeline keeps it.
    let newestRunID: String?
    /// Model catalog for a sub-agent's model name, as the diagram's capsules shorten it.
    var catalog: [ModelOption] = []
    /// The pane's open texts and follow state, kept across view switches.
    @ObservedObject var memory: MightyStructureMemory
    /// Shows a diagram node (`MightyGraphLayout.Node.id`) in the diagram.
    let onReveal: (String) -> Void

    /// Scroll bars that always show take their width from the columns.
    @ViewState private var legacyScrollers = NSScroller.preferredScrollerStyle == .legacy

    private static let chipHeight: CGFloat = 20
    private static let scrollSpace = "mighty-structure-scroll"
    /// A card's and a sub-agent card's corners.
    static let cardCorner: CGFloat = 10
    private static let nodeCorner: CGFloat = 8
    /// A sub-agent card's status bar, and the glyph column of a tool call row.
    private static let statusBarWidth: CGFloat = 3
    private static let glyphWidth: CGFloat = 14
    /// The dashed outline around a sub-agent tree.
    private static let clusterDash: [CGFloat] = [4, 3]
    /// Type sizes: a card's texts, a sub-agent's output, its prompt.
    private static let textSize: CGFloat = 12
    private static let outputSize: CGFloat = 11
    private static let promptSize: CGFloat = 10.5

    var body: some View {
        GeometryReader { geo in
            // The pane's width, read from outside the scroll view, so the columns'
            // width never feeds back into what is measured.
            let scroller = legacyScrollers ? NSScroller.scrollerWidth(for: .regular, scrollerStyle: .legacy) : 0
            let available = geo.size.width - scroller - 2 * DesignMetrics.Inset.graphBlockBodyH
            let visibleHeight = geo.size.height
            ScrollViewReader { proxy in
                ScrollView([.horizontal, .vertical]) {
                    Group {
                        switch shape {
                        case .phases(let columns):
                            phases(columns, width: MightyStructure.columnWidth(available: available, count: columns.count, gap: DesignMetrics.Spacing.lg))
                        case .free(let rows):
                            free(rows, width: MightyStructure.columnWidth(available: available, count: 3, gap: DesignMetrics.Spacing.lg))
                        }
                    }
                    .padding(.horizontal, DesignMetrics.Inset.graphBlockBodyH).padding(.vertical, DesignMetrics.Spacing.md)
                    .background(GeometryReader { content in
                        Color.clear.preference(key: MightyStructureContentFrame.self, value: content.frame(in: .named(Self.scrollSpace)))
                    })
                }
                .coordinateSpace(name: Self.scrollSpace)
                .defaultScrollAnchor(.bottomLeading)
                // Where the reader is, by the transcript's rule: near the bottom follows.
                .onPreferenceChange(MightyStructureContentFrame.self) { frame in
                    memory.following = TranscriptViewport.isFollowing(originY: Double(-frame.minY), visibleHeight: Double(visibleHeight),
                                                                      documentHeight: Double(frame.height))
                }
                .onAppear { DispatchQueue.main.async { follow(proxy) } }
                // A new request is the user's own: it is shown, as the timeline shows it.
                .onChange(of: newestRunID) { _, _ in follow(proxy) }
                // The newest card grows as its blocks arrive and settle. It stays in view
                // only for a reader at the bottom who is not reading it opened.
                .onChange(of: newestGrowth) { _, _ in
                    guard memory.following, !newestOpened else { return }
                    follow(proxy)
                }
            }
        }
        .onReceive(NotificationCenter.default.publisher(for: NSScroller.preferredScrollerStyleDidChangeNotification)) { _ in
            legacyScrollers = NSScroller.preferredScrollerStyle == .legacy
        }
        .background(Palette.raised)
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("mighty-structure-\(sessionID)")
    }

    /// One of the newest card's texts is open: its reader is not moved.
    private var newestOpened: Bool {
        guard let newestRunID, !memory.expanded.isEmpty else { return false }
        let nodes = Self.foldKey("node", MightyGraphBlockSize.nodeID(runID: newestRunID, suffix: ""))
        return memory.expanded.contains { key in
            ["request", "message", "answer"].contains { key == Self.foldKey($0, newestRunID) } || key.hasPrefix(nodes)
        }
    }

    /// The newest request with what makes its card taller or settles it: a reply by its
    /// folded lines, not by every word that streams in.
    private var newestGrowth: [String] {
        guard let newestRunID else { return [] }
        let item: MightyStructure.Item? = switch shape {
        case .phases(let columns): columns.lazy.flatMap(\.items).first { $0.runID == newestRunID }
        case .free(let rows): rows.last { $0.id == newestRunID }?.item
        }
        guard let item else { return [newestRunID] }
        return [newestRunID, item.status, item.result == nil ? "" : "result", "\(item.clusters.reduce(0) { $0 + $1.nodes.count })",
                item.activity.last?.id ?? "", item.message.map { "\($0.preview.reduce(1) { $1 == "\n" ? $0 + 1 : $0 })" } ?? ""]
    }

    private func follow(_ proxy: ScrollViewProxy) {
        guard let newestRunID else { return }
        if case .free = shape { proxy.scrollTo(newestRunID, anchor: .bottomLeading) } else { proxy.scrollTo(newestRunID, anchor: .bottom) }
    }

    // MARK: Styled pane

    private func phases(_ columns: [MightyStructure.Column], width: CGFloat) -> some View {
        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.md) {
            rail(columns.map { ($0.id, $0.phase.title, $0.step) }, width: width)
                .accessibilityElement(children: .contain)
                .accessibilityLabel(L("guidedPanel.phasesAccessibility"))
            HStack(alignment: .top, spacing: DesignMetrics.Spacing.lg) {
                ForEach(columns) { column in
                    LazyVStack(alignment: .leading, spacing: DesignMetrics.Spacing.md) {
                        ForEach(column.items) { card($0, width: width).id($0.id) }
                    }
                    .frame(width: width, alignment: .topLeading)
                    .accessibilityElement(children: .contain)
                    .accessibilityLabel(stepAccessibility(column.phase.title, column.step))
                    .accessibilityIdentifier("mighty-structure-phase-\(column.id)")
                }
            }
        }
    }

    /// The chips over the columns, each joined to the next by a line that is green once
    /// the step before it is done.
    private func rail(_ steps: [(id: String, title: String, step: MightyStructure.Step)], width: CGFloat) -> some View {
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
                .frame(width: width, alignment: .leading)
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

    private func free(_ rows: [MightyStructure.Row], width: CGFloat) -> some View {
        LazyVStack(alignment: .leading, spacing: DesignMetrics.Spacing.xl) {
            if rows.isEmpty {
                Text(L("graph.timeline.empty")).font(.system(size: 12)).foregroundStyle(Palette.ink2)
                    .padding(.vertical, DesignMetrics.Spacing.md)
            }
            ForEach(rows) { row in
                freeRow(row, width: width).id(row.id)
            }
        }
    }

    private func freeRow(_ row: MightyStructure.Row, width: CGFloat) -> some View {
        let item = row.item
        let steps: [(id: String, title: String, step: MightyStructure.Step)] = [
            ("request", L("graph.structure.column.request"), row.request),
            ("execution", L("graph.structure.column.execution"), row.execution),
            ("result", L("graph.structure.column.result"), row.result),
        ]
        return VStack(alignment: .leading, spacing: DesignMetrics.Spacing.sm) {
            Text(heading(item)).font(Palette.heading(13)).foregroundStyle(Palette.ink).lineLimit(1)
                .accessibilityAddTraits(.isHeader)
            rail(steps, width: width)
            HStack(alignment: .top, spacing: DesignMetrics.Spacing.lg) {
                // 요청
                HStack(alignment: .top, spacing: DesignMetrics.Spacing.xs) {
                    if let request = item.request {
                        foldableText(request, key: Self.foldKey("request", item.runID), lines: MightyStructure.requestLines, size: Self.textSize,
                                     width: Self.cardTextWidth(width) - DesignMetrics.Layout.hitTarget - DesignMetrics.Spacing.xs, name: steps[0].title)
                    } else {
                        Text(verbatim: " ").font(.system(size: Self.textSize)).frame(maxWidth: .infinity, alignment: .leading)
                    }
                    revealButton(item.nodeID)
                }
                .cardSurface()
                .frame(width: width, alignment: .topLeading)
                .accessibilityElement(children: .contain)
                .accessibilityLabel(stepAccessibility(steps[0].title, row.request))
                // 실행
                VStack(alignment: .leading, spacing: DesignMetrics.Spacing.sm) {
                    execution(item, width: width, titled: false)
                        .accessibilityLabel(stepAccessibility(L("graph.block.executionOf", ["n": "\(item.ordinal)"]), row.execution))
                    clusters(item.clusters, column: width)
                }
                .frame(width: width, alignment: .topLeading)
                // 결과
                VStack(alignment: .leading, spacing: DesignMetrics.Spacing.sm) {
                    HStack(spacing: DesignMetrics.Spacing.sm) {
                        resultTitle(item)
                        Spacer(minLength: 0)
                        revealButton(item.finished ? MightyGraphBlockSize.nodeID(runID: item.runID, suffix: "result") : item.executionNodeID)
                    }
                    // A reply without an answer is in 실행 already; 결과 says how the request ended.
                    outcome(item)
                }
                .cardSurface()
                .frame(width: width, alignment: .topLeading)
                .accessibilityElement(children: .contain)
                .accessibilityLabel(stepAccessibility(steps[2].title, row.result))
            }
        }
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("mighty-structure-request-\(item.runID)")
    }

    // MARK: Cards and clusters

    /// The request as the diagram draws it: "Request N · Claude" and what was asked; under
    /// it its 실행 card, with the latest reply, tool calls and status; its sub-agent trees;
    /// and once it finished, its answer.
    private func card(_ item: MightyStructure.Item, width: CGFloat) -> some View {
        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.sm) {
            VStack(alignment: .leading, spacing: DesignMetrics.Spacing.sm) {
                HStack(spacing: DesignMetrics.Spacing.sm) {
                    Text(heading(item)).font(.system(size: Self.textSize, weight: .bold)).foregroundStyle(Palette.ink).lineLimit(1)
                    Spacer(minLength: DesignMetrics.Spacing.xs)
                    revealButton(item.nodeID)
                }
                if let request = item.request {
                    foldableText(request, key: Self.foldKey("request", item.runID), lines: MightyStructure.requestLines, size: Self.textSize,
                                 width: Self.cardTextWidth(width), name: L("graph.structure.column.request"))
                }
            }
            .cardSurface()
            .accessibilityElement(children: .contain)
            .accessibilityLabel(L("graph.structure.contentAccessibility", ["name": heading(item), "text": item.input]))
            .accessibilityIdentifier("mighty-structure-request-\(item.runID)")
            execution(item, width: width, titled: true)
                .accessibilityLabel(L("graph.block.executionOf", ["n": "\(item.ordinal)"]))
            clusters(item.clusters, column: width)
            if item.finished {
                VStack(alignment: .leading, spacing: DesignMetrics.Spacing.sm) {
                    HStack(spacing: DesignMetrics.Spacing.sm) {
                        resultTitle(item)
                        Spacer(minLength: 0)
                        revealButton(MightyGraphBlockSize.nodeID(runID: item.runID, suffix: "result"))
                    }
                    // A reply without an answer is in 실행 already; the result says how the request ended.
                    outcome(item)
                }
                .cardSurface()
                .accessibilityElement(children: .contain)
                .accessibilityIdentifier("mighty-structure-result-\(item.runID)")
            }
        }
    }

    /// The 실행 part: the main agent's status, tally, latest reply and tool calls, as the
    /// diagram's 실행 block holds them. `titled` names it; a free row's rail already does.
    private func execution(_ item: MightyStructure.Item, width: CGFloat, titled: Bool) -> some View {
        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.sm) {
            HStack(spacing: DesignMetrics.Spacing.sm) {
                if titled {
                    Label(L("graph.block.executionOf", ["n": "\(item.ordinal)"]), systemImage: MightyGraphView.executionIcon)
                        .font(.system(size: Self.outputSize, weight: .bold)).foregroundStyle(Palette.ink).lineLimit(1)
                }
                MightyStatusPill(text: MightyGraphView.statusLabel(item.status), tone: DesignTone(status: item.status))
                tally(item)
                Spacer(minLength: 0)
                revealButton(item.executionNodeID)
            }
            progress(item, width: Self.cardTextWidth(width))
        }
        .cardSurface()
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("mighty-structure-execution-\(item.runID)")
    }

    /// The width a card's text has in a column of `width`.
    private static func cardTextWidth(_ width: CGFloat) -> CGFloat { width - 2 * DesignMetrics.Inset.graphBlockBodyH }

    private func tally(_ item: MightyStructure.Item) -> some View {
        Text(L("graph.structure.tally", ["agents": "\(item.tally.agents)", "tools": "\(item.tally.tools)"]))
            .font(.system(size: Self.outputSize, weight: .semibold)).foregroundStyle(Palette.ink2).lineLimit(1)
    }

    /// The main block's latest reply and its last tool calls.
    @ViewBuilder private func progress(_ item: MightyStructure.Item, width: CGFloat) -> some View {
        if let message = item.message {
            foldableText(message, key: Self.foldKey("message", item.runID), lines: MightyStructure.messageLines, size: Self.textSize,
                         width: width, name: L("graph.structure.latest"))
        } else if !item.finished && item.activity.isEmpty {
            Text(L("graph.block.waitingResponse")).font(.system(size: Self.textSize)).foregroundStyle(Palette.ink2)
        }
        if !item.activity.isEmpty {
            VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xxs) {
                ForEach(item.activity) { activityRow($0) }
            }
            .accessibilityElement(children: .contain)
            .accessibilityLabel(L("graph.structure.activity"))
        }
    }

    /// The result card's header words, in the outcome's colour once there is one.
    private func resultTitle(_ item: MightyStructure.Item) -> some View {
        let look: (title: String, icon: String) = switch item.status {
        case "error": (L("graph.block.resultError"), "exclamationmark.triangle")
        case "stopped": (L("graph.block.resultStopped"), "stop.circle")
        default: (L("graph.block.result"), "checkmark.seal")
        }
        return Label(look.title, systemImage: look.icon)
            .font(.system(size: Self.outputSize, weight: .bold)).lineLimit(1)
            .foregroundStyle(item.finished ? Palette.text(item.status) : Palette.ink2)
    }

    /// The answer as the transcript renders a reply; without one, its state (the last
    /// reply of a request that failed or stopped is in its 실행 part).
    @ViewBuilder private func outcome(_ item: MightyStructure.Item) -> some View {
        if let answer = item.answer {
            foldableMarkdown(answer, key: Self.foldKey("answer", item.runID), name: L("graph.block.result"))
        } else {
            Text(item.finished ? MightyGraphView.statusLabel(item.status) : L("graph.structure.resultPending"))
                .font(.system(size: Self.textSize)).foregroundStyle(Palette.ink2)
        }
    }

    @ViewBuilder private func clusters(_ clusters: [MightyStructure.Cluster], column: CGFloat) -> some View {
        if !clusters.isEmpty {
            // `MightyStructure.nodeTextWidth` takes these paddings off a node's width.
            VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xs) {
                ForEach(clusters) { cluster($0, column: column) }
            }
            .padding(.leading, DesignMetrics.Spacing.lg)
        }
    }

    private func cluster(_ cluster: MightyStructure.Cluster, column: CGFloat) -> some View {
        LazyVStack(alignment: .leading, spacing: DesignMetrics.Spacing.xs) {
            ForEach(cluster.nodes) { nodeCard($0, column: column) }
        }
        .padding(.horizontal, DesignMetrics.Spacing.sm).padding(.vertical, DesignMetrics.Spacing.sm)
        .overlay {
            RoundedRectangle(cornerRadius: Self.nodeCorner, style: .continuous)
                .strokeBorder(Palette.border, style: StrokeStyle(lineWidth: 1, dash: Self.clusterDash))
                .allowsHitTesting(false)
        }
        .accessibilityElement(children: .contain)
        .accessibilityLabel(L("graph.structure.clusterAccessibility", ["count": "\(cluster.nodes.count)"]))
    }

    /// A sub-agent or task as a small card: its status as a bar in its colour, its kind's
    /// glyph and ink as the diagram gives them (`MightyGraphView.agentPresentation`), its
    /// title, kind and model; then what it was asked, its latest output and its last
    /// tool call. Opened, it shows them whole and its last few tool calls.
    private func nodeCard(_ node: MightyStructure.Node, column: CGFloat) -> some View {
        let task = node.kind == "task"
        let key = Self.foldKey("node", node.nodeID)
        let open = memory.expanded.contains(key)
        // Folds are judged at the width the card's text really has at its depth.
        let textWidth = MightyStructure.nodeTextWidth(column: column, depth: node.depth)
        let folds = node.prompt?.folds(lines: MightyStructure.promptLines,
                                       charactersPerLine: MightyStructure.charactersPerLine(width: textWidth, fontSize: Self.promptSize)) == true
            || node.output?.folds(lines: MightyStructure.outputLines,
                                  charactersPerLine: MightyStructure.charactersPerLine(width: textWidth, fontSize: Self.outputSize)) == true
            || node.activity.count > 1
        let model = node.model.map { ModelUsageFormat.shortName($0, catalog: catalog, versioned: true) }
        let meta = ([MightyTimelineText.kind(node.kind)] + [model].compactMap { $0 }).joined(separator: " · ")
        let shape = RoundedRectangle(cornerRadius: Self.nodeCorner, style: .continuous)
        return VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xxs) {
            HStack(spacing: DesignMetrics.Spacing.sm) {
                Image(systemName: task ? "terminal" : "person.crop.square.filled.and.at.rectangle")
                    .font(.system(size: 9, weight: .semibold)).foregroundStyle(task ? Palette.taskText : Palette.agentText)
                Text(verbatim: node.title).font(.system(size: Self.outputSize, weight: .semibold)).foregroundStyle(Palette.ink).lineLimit(1)
                    .layoutPriority(1)
                Text(meta).font(.system(size: 10)).foregroundStyle(Palette.ink2).lineLimit(1)
                Spacer(minLength: 0)
                revealButton(node.nodeID)
            }
            if let prompt = node.prompt {
                let shown = open ? prompt.text : prompt.preview
                Text(verbatim: shown)
                    .font(.system(size: Self.promptSize)).foregroundStyle(Palette.ink2)
                    .lineLimit(open ? nil : MightyStructure.promptLines)
                    .contentText(name: L("graph.structure.prompt"), value: shown)
            }
            if let output = node.output {
                let shown = open ? output.text : output.preview
                Text(verbatim: shown)
                    .font(.system(size: Self.outputSize)).foregroundStyle(Palette.ink)
                    .lineLimit(open ? nil : MightyStructure.outputLines)
                    .contentText(name: L("graph.structure.output"), value: shown)
            }
            ForEach(open ? node.activity : Array(node.activity.suffix(1))) { activityRow($0) }
            if folds { foldToggle(key, open: open) }
        }
        // `MightyStructure.nodeTextWidth` takes this padding and the indent off too.
        .padding(.leading, DesignMetrics.Spacing.md).padding(.trailing, DesignMetrics.Spacing.xs)
        .padding(.vertical, DesignMetrics.Spacing.xs)
        .background(Palette.panel, in: shape)
        .overlay(alignment: .leading) {
            Rectangle().fill(Palette.mark(node.status)).frame(width: Self.statusBarWidth).allowsHitTesting(false).accessibilityHidden(true)
        }
        .clipShape(shape)
        .overlay { shape.strokeBorder(Palette.border, lineWidth: 1).allowsHitTesting(false) }
        .padding(.leading, CGFloat(min(node.depth, MightyStructure.maximumIndent)) * DesignMetrics.Spacing.lg)
        .accessibilityElement(children: .contain)
        .accessibilityLabel(L("graph.structure.nodeAccessibility", ["title": node.title, "status": MightyGraphView.statusLabel(node.status)]))
        .accessibilityValue(node.output?.preview ?? node.prompt?.preview ?? "")
        .accessibilityIdentifier("mighty-structure-node-\(node.nodeID)")
    }

    /// One tool call with its kind's glyph, as a diagram block's activity row draws it.
    private func activityRow(_ line: MightyStructure.ActivityLine) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: DesignMetrics.Spacing.sm) {
            Image(systemName: AgentActivityRow.symbol(line.kind))
                .font(.system(size: 10, weight: .medium)).frame(width: Self.glyphWidth)
                .foregroundStyle(line.state == "error" ? Palette.errText : line.state == "running" ? Palette.accent : Palette.ink2)
            Text(verbatim: line.text)
                .font(.system(size: Self.outputSize, design: AgentActivityRow.monospacedKinds.contains(line.kind) ? .monospaced : .default))
                .foregroundStyle(Palette.ink2).lineLimit(1).truncationMode(.tail)
                .textSelection(.enabled)
                .frame(maxWidth: .infinity, alignment: .leading)
        }
        .help(line.text)
        .accessibilityElement(children: .combine)
    }

    // MARK: Texts shown in place

    /// The key a text's open state is kept under: what it is and whose.
    static func foldKey(_ part: String, _ id: String) -> String { part + ":" + id }

    /// Plain text: its first lines folded, the whole of it (up to its cap) opened. The
    /// toggle shows whenever the folded lines may cut it at `width`.
    private func foldableText(_ excerpt: MightyStructure.Excerpt, key: String, lines: Int, size: CGFloat, width: CGFloat, name: String) -> some View {
        let open = memory.expanded.contains(key)
        let shown = open ? excerpt.text : excerpt.preview
        let folds = excerpt.folds(lines: lines, charactersPerLine: MightyStructure.charactersPerLine(width: width, fontSize: size))
        return VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xxs) {
            Text(verbatim: shown)
                .font(.system(size: size)).foregroundStyle(Palette.ink)
                .lineLimit(open ? nil : lines)
                .contentText(name: name, value: shown)
            if folds { foldToggle(key, open: open) }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    /// Markdown as the transcript renders a reply. Folded, its preview: whole lines with
    /// any open code fence closed, so nothing is cut mid-line.
    private func foldableMarkdown(_ excerpt: MightyStructure.Excerpt, key: String, name: String) -> some View {
        let open = memory.expanded.contains(key)
        return VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xxs) {
            AgentMarkdownView(source: open ? excerpt.text : excerpt.preview, compact: true)
                .foregroundStyle(Palette.ink)
                .accessibilityElement(children: .contain)
                .accessibilityLabel(name)
            if excerpt.cut { foldToggle(key, open: open) }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private func foldToggle(_ key: String, open: Bool) -> some View {
        Button {
            if !memory.expanded.insert(key).inserted { memory.expanded.remove(key) }
        } label: {
            Text(open ? L("graph.structure.less") : L("graph.structure.more"))
                .font(.system(size: Self.outputSize, weight: .bold)).foregroundStyle(Palette.accent)
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityAddTraits(.isToggle)
        .accessibilityValue(open ? L("graph.structure.expanded") : L("graph.structure.collapsed"))
        .accessibilityIdentifier("mighty-structure-more-\(key)")
    }

    /// The small header button that shows the block in the diagram.
    private func revealButton(_ nodeID: String) -> some View {
        Button { onReveal(nodeID) } label: {
            Image(systemName: "arrow.up.right.square").font(.system(size: 11, weight: .medium)).foregroundStyle(Palette.ink2)
                .frame(width: DesignMetrics.Layout.hitTarget, height: DesignMetrics.Layout.hitTarget)
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .help(L("graph.structure.reveal"))
        .accessibilityLabel(L("graph.structure.reveal"))
        .accessibilityIdentifier("mighty-structure-reveal-\(nodeID)")
    }

    private func heading(_ item: MightyStructure.Item) -> String {
        StyleChrome.requestTitle(prefix: styleTitles.prefix(item.input), ordinal: item.ordinal, providerLabel: ProviderOptions.label(provider))
    }
}

private extension View {
    /// A card of the structure: the diagram's white block card around its padded content.
    func cardSurface() -> some View {
        padding(.horizontal, DesignMetrics.Inset.graphBlockBodyH).padding(.vertical, DesignMetrics.Spacing.sm)
            .frame(maxWidth: .infinity, alignment: .topLeading)
            .mightyBlockCard(cornerRadius: MightyStructureView.cardCorner)
    }

    /// A text shown in place: wrapped to its column, selectable, and read out as what it is.
    func contentText(name: String, value: String) -> some View {
        multilineTextAlignment(.leading)
            .fixedSize(horizontal: false, vertical: true)
            .frame(maxWidth: .infinity, alignment: .leading)
            .textSelection(.enabled)
            .accessibilityLabel(name)
            .accessibilityValue(value)
    }
}

/// The structure's content frame in its scroll view's space: where the reader is.
private struct MightyStructureContentFrame: PreferenceKey {
    static let defaultValue = CGRect.zero
    static func reduce(value: inout CGRect, nextValue: () -> CGRect) { value = nextValue() }
}
