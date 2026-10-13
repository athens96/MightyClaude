import MightyCore
import SwiftUI

/// The Mighty graph drawn as its structure (design "A. 단계별 열"), from
/// `MightyStructure.make`: a styled pane's phases as a rail of chips with a column of
/// request cards under each, a free pane's requests as one 요청 → 실행 → 결과 row each.
/// Under every card its sub-agent trees sit in dashed boxes.
///
/// The cards read on their own: the request, the latest reply and the last tool calls
/// while it runs, the answer once it finished, and each sub-agent's prompt, output and
/// last tool call. A long text opens in place; the small button in a card's header
/// shows its block in the diagram, as opening a timeline row aims the diagram's camera.
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
    /// Shows a diagram node (`MightyGraphLayout.Node.id`) in the diagram.
    let onReveal: (String) -> Void

    /// Texts opened past their folded lines, by `foldKey`.
    @ViewState private var expanded = Set<String>()

    /// A tree's indent stops growing past this depth, so a deep chain stays inside its column.
    static let maximumIndent = 6
    private static let chipHeight: CGFloat = 20
    /// The height of a line of the compact Markdown the answer is drawn in
    /// (`AgentMarkdownView(compact:)`: 11pt with 2pt between lines).
    private static let compactLineHeight: CGFloat = 15

    var body: some View {
        GeometryReader { geo in
            // The pane's width, read from outside the scroll view, so the columns'
            // width never feeds back into what is measured.
            let available = geo.size.width - 2 * DesignMetrics.Inset.graphBlockBodyH
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
                }
                .defaultScrollAnchor(.bottomLeading)
                .onAppear { DispatchQueue.main.async { follow(proxy) } }
                // The newest card grows as its blocks arrive and settle; it stays in view, as
                // the timeline's newest request does.
                .onChange(of: newestGrowth) { _, _ in follow(proxy) }
            }
        }
        .background(Palette.raised)
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("mighty-structure-\(sessionID)")
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
                        ForEach(column.items) { card($0).id($0.id) }
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
                        foldableText(request, key: foldKey("request", item.runID), lines: MightyStructure.requestLines, size: 12,
                                     name: steps[0].title)
                    } else {
                        Text(verbatim: " ").font(.system(size: 12)).frame(maxWidth: .infinity, alignment: .leading)
                    }
                    revealButton(item.nodeID)
                }
                .cardSurface()
                .frame(width: width, alignment: .topLeading)
                .accessibilityElement(children: .contain)
                .accessibilityLabel(stepAccessibility(steps[0].title, row.request))
                // 실행
                VStack(alignment: .leading, spacing: DesignMetrics.Spacing.sm) {
                    VStack(alignment: .leading, spacing: DesignMetrics.Spacing.sm) {
                        HStack(spacing: DesignMetrics.Spacing.sm) {
                            MightyStatusPill(text: MightyGraphView.statusLabel(item.status), tone: DesignTone(status: item.status))
                            tally(item)
                            Spacer(minLength: 0)
                            revealButton(item.nodeID)
                        }
                        progress(item)
                    }
                    .cardSurface()
                    .accessibilityElement(children: .contain)
                    .accessibilityLabel(stepAccessibility(steps[1].title, row.execution))
                    clusters(item.clusters)
                }
                .frame(width: width, alignment: .topLeading)
                // 결과
                VStack(alignment: .leading, spacing: DesignMetrics.Spacing.sm) {
                    HStack(spacing: DesignMetrics.Spacing.sm) {
                        resultTitle(item)
                        Spacer(minLength: 0)
                        revealButton(MightyGraphBlockSize.nodeID(runID: item.runID, suffix: item.finished ? "result" : "request"))
                    }
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

    /// "Request N · Claude" and its status; the request; while it runs its latest reply
    /// and tool calls, once it finished its answer; then its sub-agent trees.
    private func card(_ item: MightyStructure.Item) -> some View {
        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.sm) {
            VStack(alignment: .leading, spacing: DesignMetrics.Spacing.sm) {
                HStack(spacing: DesignMetrics.Spacing.sm) {
                    Text(heading(item)).font(.system(size: 12, weight: .bold)).foregroundStyle(Palette.ink).lineLimit(1)
                    Spacer(minLength: DesignMetrics.Spacing.xs)
                    MightyStatusPill(text: MightyGraphView.statusLabel(item.status), tone: DesignTone(status: item.status))
                    revealButton(item.nodeID)
                }
                if let request = item.request {
                    foldableText(request, key: foldKey("request", item.runID), lines: MightyStructure.requestLines, size: 12,
                                 name: L("graph.structure.column.request"))
                }
                Divider().overlay(Palette.border)
                if item.finished {
                    resultTitle(item)
                    outcome(item)
                } else {
                    tally(item)
                    progress(item)
                }
            }
            .cardSurface()
            .accessibilityElement(children: .contain)
            .accessibilityLabel(L("graph.structure.contentAccessibility", ["name": heading(item), "text": item.input]))
            .accessibilityIdentifier("mighty-structure-request-\(item.runID)")
            clusters(item.clusters)
        }
    }

    private func tally(_ item: MightyStructure.Item) -> some View {
        Text(L("graph.structure.tally", ["agents": "\(item.tally.agents)", "tools": "\(item.tally.tools)"]))
            .font(.system(size: 11, weight: .semibold)).foregroundStyle(Palette.ink2).lineLimit(1)
    }

    /// The main block's latest reply and its last tool calls.
    @ViewBuilder private func progress(_ item: MightyStructure.Item) -> some View {
        if let message = item.message {
            foldableText(message, key: foldKey("message", item.runID), lines: MightyStructure.messageLines, size: 12,
                         name: L("graph.structure.latest"))
        } else if !item.finished && item.activity.isEmpty {
            Text(L("graph.block.waitingResponse")).font(.system(size: 12)).foregroundStyle(Palette.ink2)
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
            .font(.system(size: 11, weight: .bold)).lineLimit(1)
            .foregroundStyle(item.finished ? Palette.text(item.status) : Palette.ink2)
    }

    /// The answer as the transcript renders a reply; without one, the last reply of a
    /// request that failed or stopped, else its state.
    @ViewBuilder private func outcome(_ item: MightyStructure.Item) -> some View {
        if let answer = item.answer {
            foldableMarkdown(answer, key: foldKey("answer", item.runID), lines: MightyStructure.answerLines, name: L("graph.block.result"))
        } else if item.finished, let message = item.message {
            foldableText(message, key: foldKey("message", item.runID), lines: MightyStructure.messageLines, size: 12,
                         name: L("graph.structure.latest"))
        } else {
            Text(item.finished ? MightyGraphView.statusLabel(item.status) : L("graph.structure.resultPending"))
                .font(.system(size: 12)).foregroundStyle(Palette.ink2)
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
        LazyVStack(alignment: .leading, spacing: DesignMetrics.Spacing.xs) {
            ForEach(cluster.nodes) { nodeCard($0) }
        }
        .padding(.horizontal, DesignMetrics.Spacing.sm).padding(.vertical, DesignMetrics.Spacing.sm)
        .overlay {
            RoundedRectangle(cornerRadius: 8, style: .continuous)
                .strokeBorder(Palette.border, style: StrokeStyle(lineWidth: 1, dash: [4, 3]))
                .allowsHitTesting(false)
        }
        .accessibilityElement(children: .contain)
        .accessibilityLabel(L("graph.structure.clusterAccessibility", ["count": "\(cluster.nodes.count)"]))
    }

    /// A sub-agent or task as a small card: its status as a bar in its colour, its kind's
    /// glyph and ink as the diagram gives them (`MightyGraphView.agentPresentation`), its
    /// title, kind and model; then what it was asked, its latest output and its last
    /// tool call. Opened, it shows them whole and its last few tool calls.
    private func nodeCard(_ node: MightyStructure.Node) -> some View {
        let task = node.kind == "task"
        let key = foldKey("node", node.nodeID)
        let open = expanded.contains(key)
        let folds = node.prompt?.folds == true || node.output?.folds == true || node.activity.count > 1
        let model = node.model.map { ModelUsageFormat.shortName($0, catalog: catalog, versioned: true) }
        let meta = ([MightyTimelineText.kind(node.kind)] + [model].compactMap { $0 }).joined(separator: " · ")
        let shape = RoundedRectangle(cornerRadius: 8, style: .continuous)
        return VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xxs) {
            HStack(spacing: DesignMetrics.Spacing.sm) {
                Image(systemName: task ? "terminal" : "person.crop.square.filled.and.at.rectangle")
                    .font(.system(size: 9, weight: .semibold)).foregroundStyle(task ? Palette.taskText : Palette.agentText)
                Text(verbatim: node.title).font(.system(size: 11, weight: .semibold)).foregroundStyle(Palette.ink).lineLimit(1)
                    .layoutPriority(1)
                Text(meta).font(.system(size: 10)).foregroundStyle(Palette.ink2).lineLimit(1)
                Spacer(minLength: 0)
                revealButton(node.nodeID)
            }
            if let prompt = node.prompt {
                let shown = open ? prompt.text : prompt.preview
                Text(verbatim: shown)
                    .font(.system(size: 10.5)).foregroundStyle(Palette.ink2)
                    .lineLimit(open ? nil : MightyStructure.promptLines)
                    .contentText(name: L("graph.structure.prompt"), value: shown)
            }
            if let output = node.output {
                let shown = open ? output.text : output.preview
                Text(verbatim: shown)
                    .font(.system(size: 11)).foregroundStyle(Palette.ink)
                    .lineLimit(open ? nil : MightyStructure.outputLines)
                    .contentText(name: L("graph.structure.output"), value: shown)
            }
            ForEach(open ? node.activity : Array(node.activity.suffix(1))) { activityRow($0) }
            if folds { foldToggle(key, open: open) }
        }
        .padding(.leading, DesignMetrics.Spacing.md).padding(.trailing, DesignMetrics.Spacing.xs)
        .padding(.vertical, DesignMetrics.Spacing.xs)
        .background(Palette.panel, in: shape)
        .overlay(alignment: .leading) {
            Rectangle().fill(Palette.mark(node.status)).frame(width: 3).allowsHitTesting(false).accessibilityHidden(true)
        }
        .clipShape(shape)
        .overlay { shape.strokeBorder(Palette.border, lineWidth: 1).allowsHitTesting(false) }
        .padding(.leading, CGFloat(min(node.depth, Self.maximumIndent)) * DesignMetrics.Spacing.lg)
        .accessibilityElement(children: .contain)
        .accessibilityLabel(L("graph.structure.nodeAccessibility", ["title": node.title, "status": MightyGraphView.statusLabel(node.status)]))
        .accessibilityValue(node.output?.preview ?? node.prompt?.preview ?? "")
        .accessibilityIdentifier("mighty-structure-node-\(node.nodeID)")
    }

    /// One tool call with its kind's glyph, as a diagram block's activity row draws it.
    private func activityRow(_ line: MightyStructure.ActivityLine) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: DesignMetrics.Spacing.sm) {
            Image(systemName: AgentActivityRow.symbol(line.kind))
                .font(.system(size: 10, weight: .medium)).frame(width: 14)
                .foregroundStyle(line.state == "error" ? Palette.errText : line.state == "running" ? Palette.accent : Palette.ink2)
            Text(verbatim: line.text)
                .font(.system(size: 11, design: AgentActivityRow.monospacedKinds.contains(line.kind) ? .monospaced : .default))
                .foregroundStyle(Palette.ink2).lineLimit(1).truncationMode(.tail)
                .textSelection(.enabled)
                .frame(maxWidth: .infinity, alignment: .leading)
        }
        .help(line.text)
        .accessibilityElement(children: .combine)
    }

    // MARK: Texts shown in place

    private func foldKey(_ part: String, _ id: String) -> String { part + ":" + id }

    /// Plain text: its first lines folded, the whole of it (up to its cap) opened.
    private func foldableText(_ excerpt: MightyStructure.Excerpt, key: String, lines: Int, size: CGFloat, name: String) -> some View {
        let open = expanded.contains(key)
        let shown = open ? excerpt.text : excerpt.preview
        return VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xxs) {
            Text(verbatim: shown)
                .font(.system(size: size)).foregroundStyle(Palette.ink)
                .lineLimit(open ? nil : lines)
                .contentText(name: name, value: shown)
            if excerpt.folds { foldToggle(key, open: open) }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    /// Markdown as the transcript renders a reply. Folded, only the preview's lines are
    /// laid out, and no taller than the folded lines.
    private func foldableMarkdown(_ excerpt: MightyStructure.Excerpt, key: String, lines: Int, name: String) -> some View {
        let open = expanded.contains(key)
        return VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xxs) {
            Group {
                if open || !excerpt.folds {
                    AgentMarkdownView(source: excerpt.text, compact: true)
                } else {
                    AgentMarkdownView(source: excerpt.preview, compact: true)
                        .frame(maxHeight: CGFloat(lines) * Self.compactLineHeight, alignment: .top)
                        .clipped()
                }
            }
            .foregroundStyle(Palette.ink)
            .accessibilityElement(children: .contain)
            .accessibilityLabel(name)
            if excerpt.folds { foldToggle(key, open: open) }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private func foldToggle(_ key: String, open: Bool) -> some View {
        Button {
            if !expanded.insert(key).inserted { expanded.remove(key) }
        } label: {
            Text(open ? L("graph.structure.less") : L("graph.structure.more"))
                .font(.system(size: 11, weight: .bold)).foregroundStyle(Palette.accent)
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
            .mightyBlockCard(cornerRadius: 10)
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
