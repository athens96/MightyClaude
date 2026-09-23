import AppKit
import MightyCore
import SwiftUI

struct MightyGraphView: View {
    let sessionID: String
    let provider: String
    let runs: [MightyGraphRun]
    let draft: String
    let running: Bool
    var blockSizes: [String: MightyGraphBlockSize] = [:]
    var onSaveBlockSize: (String, MightyGraphBlockSize?) -> Void = { _, _ in }
    /// Local project folder for resolving file references; nil disables links.
    var workspaceRoot: URL? = nil
    /// Request-block titles, icons and colours come from every style this pane
    /// may run, not only the one it is in: a pane keeps blocks it made under an
    /// earlier style (§1.10). Empty for a pane that runs no style at all.
    var styleTitles: StyleRequestTitles = StyleRequestTitles()
    /// The pane's own style, for the header alone.
    var styleName: String? = nil
    var styleSource: StyleSource? = nil
    var stylePhase: String? = nil
    /// Model catalog for short-name resolution in all usage capsules and activity suffixes.
    var catalog: [ModelOption] = []
    var graphResultSize: MightyGraphBlockSize? = nil
    var onSaveResultSize: (MightyGraphBlockSize?) -> Void = { _ in }
    let onFocus: () -> Void
    @ViewState private var resized: [String: MightyGraphBlockSize] = [:]
    @ViewState private var reference: MightyGraphReference?
    @ViewState private var referenceOnLeft = false
    // Remembered across sessions and panes. Height 0 means "as tall as the graph".
    @AppStorage("mighty.referenceBubble.width") private var bubbleWidth: Double = MightyGraphReferenceBubble.defaultWidth
    @AppStorage("mighty.referenceBubble.height") private var bubbleHeight: Double = 0
    @ViewState private var expanded = Set<String>()
    @ViewState private var zoom: CGFloat = 1
    @ViewState private var scrollTarget: MightyGraphScrollTarget?
    @ViewState private var selectedNodeID: String?
    /// Counts history trims, so a second trim admits a second re-aim.
    @ViewState private var trimSequence = 0
    @StateObject private var resultFiles = MightyGraphResultFilesModel()
    @ViewState private var canvasViewport: CGSize?

    /// Kept out of the view body: long concatenations of conditionals are
    /// slow for older type checkers.
    static func headerSummary(runs: Int, agents: Int, tasks: Int, steers: Int, compactions: Int, questions: Int = 0, tokens: GraphTokenUsage) -> String {
        var parts = ["요청 \(runs)", "하위 에이전트 \(agents)"]
        if questions > 0 { parts.append("질문 \(questions)") }
        if tasks > 0 { parts.append("백그라운드 작업 \(tasks)") }
        if steers > 0 { parts.append("중간 요청 \(steers)") }
        if compactions > 0 { parts.append("컨텍스트 정리 \(compactions)") }
        if !tokens.isEmpty { parts.append(tokens.summary) }
        return parts.joined(separator: " · ")
    }

    /// Title, symbol and tint for a child block by kind.
    static func agentPresentation(_ agent: MightyGraphAgent) -> (title: String, icon: String, tint: Color) {
        // The title comes from the core so the phone's Mighty view names the
        // same block the same way; only the symbol and tint are Mac-only.
        let title = MightyGraphSupport.blockTitle(agent)
        if agent.isSteer { return (title, "text.bubble", .orange) }
        if agent.isCompact { return (title, "arrow.down.right.and.arrow.up.left", .mint) }
        if agent.isQuestion { return (title, "questionmark.bubble.fill", .indigo) }
        if agent.isTask { return (title, "terminal", .teal) }
        return (title, "person.crop.square.filled.and.at.rectangle", .purple)
    }

    /// The result card of the last finished run - the same card Core treats as
    /// the latest one, whether it completed, failed or stopped.
    private var latestResultNodeID: String? {
        runs.indices.last(where: { MightyGraphLayout.finished(runs[$0]) })
            .map { MightyGraphLayout.nodeID(runs[$0], suffix: "result") }
    }

    /// Mirrors MightyGraphLayout.fittedResultID for the card headers. Reading it
    /// off `layout` would rebuild the whole layout once per drawn card.
    private var fittedResultID: String? {
        (canvasViewport != nil && graphResultSize == nil) ? latestResultNodeID : nil
    }

    private var layout: MightyGraphLayout { .make(runs: runs, draft: draft, running: running, expanded: expanded, blockSizes: blockSizes.merging(resized) { _, new in new }, resultFilesRunID: resultFiles.selectedRunID, viewport: canvasViewport, sharedResultSize: graphResultSize) }

    var body: some View {
        let graph = layout
        let agentCount = runs.reduce(0) { $0 + $1.agents.filter { !$0.isTask && !$0.isSteer && !$0.isCompact && !$0.isQuestion }.count }
        let questionCount = runs.reduce(0) { $0 + $1.agents.filter(\.isQuestion).count }
        let taskCount = runs.reduce(0) { $0 + $1.agents.filter(\.isTask).count }
        let steerCount = runs.reduce(0) { $0 + $1.agents.filter(\.isSteer).count }
        let compactCount = runs.reduce(0) { $0 + $1.agents.filter(\.isCompact).count }
        let tokens = runs.reduce(GraphTokenUsage()) { $0 + ($1.totalUsage ?? GraphTokenUsage()) }
        // Hoisted out of the canvas call: older type checkers spend a long time
        // on optional maps and conditionals written inline in an argument list.
        let overlayView: AnyView = reference.map { AnyView(referenceOverlay($0)) } ?? AnyView(EmptyView())
        let overlayLayout: MightyOverlayLayout? = reference == nil ? nil
            : MightyOverlayLayout(onLeft: referenceOnLeft, storedWidth: bubbleWidth, storedHeight: bubbleHeight)
        let fallbackNodeID = initialTarget(graph)
        let target = scrollTarget ?? MightyGraphScrollTarget(token: "initial:" + sessionID, nodeID: fallbackNodeID, alignTop: false)
        VStack(spacing: 0) {
            HStack(spacing: 10) {
                Label(StyleChrome.graphHeader(styleName: styleName, phaseTitle: stylePhase), systemImage: "point.3.connected.trianglepath.dotted")
                    .font(.system(size: 12, weight: .semibold))
                if let styleSource, let badge = StyleChrome.sourceBadge(styleSource) { SourceBadge(text: badge) }
                Text(Self.headerSummary(runs: runs.count, agents: agentCount, tasks: taskCount, steers: steerCount, compactions: compactCount, questions: questionCount, tokens: tokens))
                    .font(.system(size: 10)).foregroundStyle(.secondary).lineLimit(1)
                    .help(tokens.isEmpty ? "" : "이 실행 창의 모든 요청 합계 · " + tokens.detail)
                Spacer(minLength: 8)
                Button { zoom = max(0.5, zoom - 0.1) } label: { Image(systemName: "minus.magnifyingglass") }
                    .disabled(zoom <= 0.5).help("축소").accessibilityIdentifier("mighty-zoom-out-\(sessionID)")
                Button { zoom = 1 } label: { Text("\(Int((zoom * 100).rounded()))%").monospacedDigit().frame(width: 38) }
                    .help("실제 크기").accessibilityIdentifier("mighty-zoom-reset-\(sessionID)")
                Button { zoom = min(1.5, zoom + 0.1) } label: { Image(systemName: "plus.magnifyingglass") }
                    .disabled(zoom >= 1.5).help("확대").accessibilityIdentifier("mighty-zoom-in-\(sessionID)")
            }
            .buttonStyle(.plain).padding(.horizontal, 12).padding(.vertical, 10)
            Divider()
            MightyGraphCanvas(graph: graph, zoom: zoom, sessionID: sessionID,
                              scrollTarget: target,
                              defaultNodeID: fallbackNodeID, selection: $selectedNodeID, edges: graphEdges(graph),
                              overlay: overlayView,
                              overlayLayout: overlayLayout,
                              onOverlayResize: { size, _ in
                                  // .zero is the corner's double click: back to the default size.
                                  if size == .zero { bubbleWidth = MightyGraphReferenceBubble.defaultWidth; bubbleHeight = 0 }
                                  else { bubbleWidth = Double(size.width); bubbleHeight = Double(size.height) }
                              },
                              onResize: resize, onResetSize: resetSize,
                              onStranded: { reaim(graph, after: $0) }, newestRunID: runs.last?.id, card: card)
                // The whole id list, not just the last one: dropping the oldest
                // runs moves every surviving card up without touching the last
                // id, and nothing else would re-aim the camera.
                .onChange(of: runs.map(\.id)) { previous, current in
                    if let last = current.last, previous.last != last {
                        scrollTarget = MightyGraphScrollTarget(token: "run:" + last, nodeID: MightyGraphBlockSize.nodeID(runID: last, suffix: "request"), alignTop: true)
                        return
                    }
                    publish(MightyGraphCamera.trimAnchor(previousRunIDs: previous, runIDs: current, selectedNodeID: selectedNodeID,
                                                         layoutNodeIDs: Set(graph.nodes.map(\.id))))
                }
                .background(GeometryReader { geo in
                    Color.clear
                        .onAppear { canvasViewport = geo.size }
                        .onChange(of: geo.size) { _, new in canvasViewport = new }
                })
                .onChange(of: canvasViewport) { _, _ in
                    // Core decides whether a card is fitting; a resize re-aims at it.
                    if let fitted = layout.fittedResultID {
                        trimSequence += 1
                        scrollTarget = MightyGraphScrollTarget(token: "fit-resize:\(trimSequence)", nodeID: fitted, alignTop: true)
                    }
                }
        }
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("mighty-graph-\(sessionID)")
        .task(id: MightyGraphResultFilesModel.Request(sessionID: sessionID, runs: runs, root: workspaceRoot)) {
            await resultFiles.load(.init(sessionID: sessionID, runs: runs, root: workspaceRoot))
        }
    }

    /// Every camera re-aim goes through one token sequence, so a second one
    /// lands even when it picks the block the first one did.
    private func publish(_ anchor: MightyGraphCamera.Anchor) {
        guard case .reaim(let nodeID, let alignTop) = anchor else { return }
        trimSequence += 1
        scrollTarget = MightyGraphScrollTarget(token: MightyGraphCamera.trimToken(sequence: trimSequence, nodeID: nodeID), nodeID: nodeID, alignTop: alignTop)
    }

    /// The layout moved every card off screen while the camera stood still —
    /// a trim, a collapsed card, anything else that shortens the document.
    private func reaim(_ graph: MightyGraphLayout, after loss: MightyGraphCamera.StrandedWatch.Loss) {
        // A request that arrived since the detection aims the camera itself.
        guard runs.last?.id == loss.newestRunID else { return }
        let nodeIDs = Set(graph.nodes.map(\.id))
        // Back to the card they were reading, not somewhere they never were.
        if nodeIDs.contains(loss.lookedAtNodeID) { publish(.reaim(nodeID: loss.lookedAtNodeID, alignTop: false)); return }
        let anchor = MightyGraphCamera.reaimAnchor(newestRunID: loss.newestRunID, selectedNodeID: selectedNodeID, layoutNodeIDs: nodeIDs)
        // The net fires once per loss, so holding here would leave the blank.
        let fallback = initialTarget(graph)
        if case .hold = anchor, nodeIDs.contains(fallback) { publish(.reaim(nodeID: fallback, alignTop: true)); return }
        publish(anchor)
    }

    /// Rendered inside the canvas's native overlay view, which is sized to
    /// `MightyGraphReferenceBubble.frame` from the canvas's own bounds.
    private func referenceOverlay(_ reference: MightyGraphReference) -> some View {
        MightyGraphReferenceBubble(sessionID: sessionID, reference: reference, onLeft: referenceOnLeft,
                                   onFlip: { referenceOnLeft.toggle() }, onClose: { self.reference = nil })
    }

    /// A clicked path resolves inside the workspace only. Unresolved paths
    /// still open the bubble so the user sees why nothing was shown.
    private func openReference(_ path: String, line: Int?) {
        onFocus()
        reference = MightyGraphReference(path: path, line: line, url: ReferenceLinkSupport.resolve(path, root: workspaceRoot))
    }

    private func resize(_ id: String, _ size: CGSize, _ finished: Bool) {
        guard let value = MightyGraphBlockSize(width: size.width, height: size.height).normalized else { return }
        resized[id] = value
        if finished {
            if id == latestResultNodeID { onSaveResultSize(value) }
            else { onSaveBlockSize(id, value) }
        }
    }

    private func resetSize(_ id: String) {
        resized.removeValue(forKey: id)
        expanded.remove(id)
        onSaveBlockSize(id, nil)
    }

    private func initialTarget(_ graph: MightyGraphLayout) -> String {
        if graph.nodes.contains(where: { $0.content == .draft }) { return MightyGraphCamera.pendingNodeID }
        return runs.last.map { MightyGraphLayout.nodeID($0, suffix: "request") } ?? MightyGraphCamera.pendingNodeID
    }

    private func graphEdges(_ graph: MightyGraphLayout) -> some View {
        return Path { path in
            for edge in graph.edges {
                let points = graph.route(edge)
                guard let start = points.first, let end = points.last else { continue }
                path.move(to: start)
                for point in points.dropFirst() { path.addLine(to: point) }
                path.move(to: CGPoint(x: end.x - 4, y: end.y - 6))
                path.addLine(to: end)
                path.addLine(to: CGPoint(x: end.x + 4, y: end.y - 6))
            }
        }
        .stroke(Palette.accent.opacity(0.6), style: StrokeStyle(lineWidth: 1.5, lineCap: .round, lineJoin: .round))
        // Routed in node coordinates, drawn in the container's, which starts at
        // the diagram's leading edge rather than at x = 0.
        .offset(x: MightyGraphCamera.drawnX(nodeX: 0, originX: graph.originX))
        .allowsHitTesting(false).accessibilityHidden(true)
    }

    @ViewBuilder private func card(_ node: MightyGraphLayout.Node) -> some View {
        switch node.content {
        case .draft:
            VStack(alignment: .leading, spacing: 10) {
                HStack {
                    Label(runs.isEmpty ? "첫 요청" : "다음 요청", systemImage: "square.and.pencil").font(.system(size: 12, weight: .semibold))
                    Spacer()
                    Text(draft.isEmpty ? "입력 대기" : "작성 중").font(.system(size: 11)).foregroundStyle(.secondary)
                }
                // A native selectable view, not Text(...).textSelection: the
                // canvas monitor hands first responder back to itself for every
                // click that is not on an NSTextView, which left a SwiftUI
                // selection here drawn but impossible to copy.
                MightyGraphDraftPreview(draft: draft, width: max(1, node.frame.width - 32), identifier: "mighty-draft-\(sessionID)")
                Spacer(minLength: 0)
            }
            .padding(16).background(Palette.panel, in: RoundedRectangle(cornerRadius: 12))
            .overlay { RoundedRectangle(cornerRadius: 12).stroke(Palette.accent.opacity(0.5), style: StrokeStyle(lineWidth: 1, dash: [5, 4])) }
            .accessibilityElement(children: .contain).accessibilityIdentifier("mighty-node-\(node.id)")
        case .request(let index):
            let run = runs[index]
            let title = StyleChrome.requestTitle(prefix: styleTitles.prefix(run.input), ordinal: index + 1,
                                                 providerLabel: ProviderOptions.label(provider))
            let icon = styleTitles.icon(run.input)?.rawValue ?? StyleIcon.requestDefault.rawValue
            let runID = run.sourceRunID ?? run.id
            let runChildBlocks = GraphChildBlocks.map(responseRecords: run.responseRecords, agents: run.agents, runId: runID)
            transcriptCard(node, title: title, icon: icon, status: run.status,
                           input: run.input, entries: run.rootEntries, tint: Palette.tint(styleTitles.tint(run.input)), usage: run.usage,
                           records: run.responseRecords ?? [], nodeModelLabel: run.nodeModelLabel, childBlocks: runChildBlocks)
        case .agent(let runIndex, let agentIndex):
            let run = runs[runIndex]
            let agent = run.agents[agentIndex]
            let look = Self.agentPresentation(agent)
            let runID = run.sourceRunID ?? run.id
            let agentChildBlocks = GraphChildBlocks.map(responseRecords: agent.responseRecords, agents: run.agents, runId: runID)
            transcriptCard(node, title: look.title, icon: look.icon, status: agent.status, input: agent.input, entries: agent.entries, tint: look.tint, usage: agent.usage,
                           records: agent.responseRecords ?? [], childBlocks: agentChildBlocks)
        case .result(let index):
            let run = runs[index]
            let failed = ["error", "failed"].contains(run.status)
            let stopped = ["stopped", "cancelled", "interrupted"].contains(run.status)
            let status = failed ? "error" : stopped ? "stopped" : "completed"
            transcriptCard(node, title: failed ? "요청 실패" : stopped ? "요청 중단" : "최종 결과", icon: failed ? "exclamationmark.triangle" : stopped ? "stop.circle" : "checkmark.seal",
                           status: status, input: "", entries: run.resultEntries, tint: failed ? .red : stopped ? .orange : .green,
                           usage: run.totalUsage, usageLabel: "요청 전체 합계", resultFilesRunID: run.status == "completed" ? run.id : nil)
        case .resultFiles(let index):
            MightyGraphResultFilesView(nodeID: node.id, files: resultFiles.files(for: runs[index].id),
                                       onOpen: { openReference($0.path, line: $0.line) }, onClose: { resultFiles.close() })
        }
    }

    private func transcriptCard(_ node: MightyGraphLayout.Node, title: String, icon: String, status: String, input: String, entries: [LogEntry], tint: Color,
                                usage: GraphTokenUsage? = nil, usageLabel: String = "이 블록", resultFilesRunID: String? = nil,
                                records: [GraphResponseRecord] = [], nodeModelLabel: String? = nil,
                                childBlocks: [String: GraphChildBlock] = [:]) -> some View {
        let content = entries.filter { $0.kind != "user" }
        return VStack(spacing: 0) {
            HStack(spacing: 7) {
                Image(systemName: icon).foregroundStyle(tint)
                Text(title).font(.system(size: 12, weight: .semibold)).lineLimit(1).help(title)
                Spacer(minLength: 3)
                if selectedNodeID == node.id { blockScrollLabel(node.id) }
                MightyGraphActivityIndicator(status: status, tint: tint)
                Text(statusLabel(status)).font(.system(size: 10)).foregroundStyle(.secondary)
                if let capsuleText = ModelUsageFormat.blockCapsule(usage: usage, records: records, nodeModelLabel: nodeModelLabel, catalog: catalog) {
                    let helpText = records.isEmpty ? (usage.map { usageLabel + " · " + $0.detail } ?? "") : ModelUsageFormat.blockCapsuleHelp(records: records, catalog: catalog)
                    Text(capsuleText).font(.system(size: 10, design: .monospaced)).foregroundStyle(.secondary).lineLimit(1)
                        .padding(.horizontal, 6).padding(.vertical, 2).background(Palette.subtle, in: Capsule())
                        .help(helpText)
                        .accessibilityLabel(helpText)
                        .accessibilityIdentifier("mighty-tokens-\(node.id)")
                }
                if let resultFilesRunID, !resultFiles.files(for: resultFilesRunID).isEmpty {
                    Button { resultFiles.toggle(resultFilesRunID) } label: {
                        HStack(spacing: 3) {
                            Image(systemName: "doc.on.doc")
                            Text("\(resultFiles.files(for: resultFilesRunID).count)").font(.system(size: 10)).monospacedDigit()
                        }
                        .foregroundStyle(resultFiles.selectedRunID == resultFilesRunID ? Palette.accent : Color.secondary)
                    }
                    .buttonStyle(.plain).help(resultFiles.selectedRunID == resultFilesRunID ? "파일 목록 닫기" : "결과에 나온 파일 보기")
                    .accessibilityLabel("결과 파일 \(resultFiles.files(for: resultFilesRunID).count)개 · 목록 토글")
                    .accessibilityIdentifier("mighty-result-files-toggle-\(node.id)")
                }
                let isLatestResult = node.id == latestResultNodeID
                // Core owns the rule; the header only reads the same answer.
                let isFittedResult = node.id == fittedResultID
                let hasSharedSize = isLatestResult && graphResultSize != nil
                if hasSharedSize {
                    Button(L("graph.result.fitToWindow")) {
                        resized.removeValue(forKey: node.id)
                        onSaveResultSize(nil)
                    }
                        .buttonStyle(.plain)
                        .font(.system(size: 10))
                        .foregroundStyle(Palette.accent)
                        .accessibilityIdentifier("mighty-fit-result-\(node.id)")
                }
                if !isFittedResult {
                    Button {
                        resized.removeValue(forKey: node.id)
                        onSaveBlockSize(node.id, nil)
                        if !expanded.insert(node.id).inserted { expanded.remove(node.id) }
                    } label: { Image(systemName: expanded.contains(node.id) ? "rectangle.compress.vertical" : "rectangle.expand.vertical") }
                        .buttonStyle(.plain).help(expanded.contains(node.id) ? "내용 접기" : "내용 더 보기")
                        .accessibilityLabel(expanded.contains(node.id) ? "내용 접기" : "내용 더 보기")
                        .accessibilityIdentifier("mighty-expand-\(node.id)")
                }
            }.padding(.horizontal, 12).frame(height: 38)
            Divider()
            if !input.isEmpty {
                MightyGraphInputPreview(input: input, width: node.frame.width - 24, identifier: "mighty-request-\(node.id)")
                    .padding(.horizontal, 12).padding(.vertical, 8)
                    .background(tint.opacity(0.055))
                Divider()
            }
            if content.isEmpty {
                Text(MightyGraphLayout.terminal(status) ? "별도의 응답 내용이 없습니다." : "에이전트 응답을 기다리고 있습니다…")
                    .font(.system(size: 12)).foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading).padding(15)
            } else {
                AgentTranscriptView(sessionId: "graph-\(sessionID)-\(node.id)", provider: provider,
                    running: !MightyGraphLayout.terminal(status), entries: content, onFocus: onFocus,
                    onReference: workspaceRoot == nil ? nil : { path, line in openReference(path, line: line) },
                    records: records, childBlocks: childBlocks, catalog: catalog)
            }
        }
        .background(Palette.panel, in: RoundedRectangle(cornerRadius: 12))
        .clipShape(RoundedRectangle(cornerRadius: 12))
        .overlay { RoundedRectangle(cornerRadius: 12).stroke(tint.opacity(0.45), lineWidth: 1) }
        .overlay { MightyGraphActivityOutline(status: status, tint: tint).allowsHitTesting(false) }
        .accessibilityElement(children: .contain).accessibilityIdentifier("mighty-node-\(node.id)")
    }

    private func blockScrollLabel(_ id: String) -> some View {
        Text("블록 스크롤").font(.system(size: 9, weight: .medium)).foregroundStyle(Palette.accent)
            .lineLimit(1).fixedSize().accessibilityIdentifier("mighty-block-scroll-" + id)
    }

    private func statusLabel(_ status: String) -> String {
        switch status {
        case "completed": "완료"
        case "error", "failed": "실패"
        case "stopped", "cancelled", "interrupted": "중단"
        case "waiting": "대기 중"
        case "starting", "queued": "준비 중"
        default: "실행 중"
        }
    }
}


/// Only cards close to the viewport own an NSTextView. Offscreen transparent
/// anchors keep node identity stable across long histories.
private struct MightyGraphCanvas<Card: View, Edges: View>: View {
    let graph: MightyGraphLayout
    let zoom: CGFloat
    let sessionID: String
    let scrollTarget: MightyGraphScrollTarget?
    /// Aimed at when the target's block no longer exists, so a camera is never
    /// committed from the document origin after a block disappeared.
    let defaultNodeID: String
    @Binding var selection: String?
    let edges: Edges
    var overlay: AnyView = AnyView(EmptyView())
    var overlayLayout: MightyOverlayLayout? = nil
    var onOverlayResize: (CGSize, Bool) -> Void = { _, _ in }
    let onResize: (String, CGSize, Bool) -> Void
    let onResetSize: (String) -> Void
    var onStranded: (MightyGraphCamera.StrandedWatch.Loss) -> Void = { _ in }
    var newestRunID: String? = nil
    let card: (MightyGraphLayout.Node) -> Card
    @ViewState private var cameraOffset: CGPoint?
    @Environment(\.colorScheme) private var colorScheme

    var body: some View {
        GeometryReader { viewport in
            let requested = graph.nodes.first(where: { $0.id == scrollTarget?.nodeID })
            let target = requested ?? graph.nodes.first(where: { $0.id == defaultNodeID })
            let targetFrame = target?.frame
            // Top alignment belongs to the block that was asked for. The
            // fallback is a different block, so it is simply centred.
            let alignTop = requested != nil && (scrollTarget?.alignTop ?? false)
            let initialOffset = targetFrame.map { MightyGraphLayout.cameraOffset(for: $0, viewport: viewport.size, zoom: zoom, alignTop: alignTop) } ?? .zero
            let displayedOffset = cameraOffset ?? initialOffset
            let panBinding = Binding<CGPoint>(get: { cameraOffset ?? initialOffset }, set: { cameraOffset = $0 })
            let visible = CGRect(x: floor(-displayedOffset.x / zoom / 64) * 64 - 192,
                                 y: floor(-displayedOffset.y / zoom / 64) * 64 - 192,
                                 width: viewport.size.width / zoom + 448,
                                 height: viewport.size.height / zoom + 448)
            let diagram = ZStack(alignment: .topLeading) {
                ZStack(alignment: .topLeading) {
                    edges
                    ForEach(graph.nodes) { node in
                        ZStack {
                            if visible.intersects(node.frame) {
                                card(node)
                                    .overlay {
                                        if selection == node.id {
                                            RoundedRectangle(cornerRadius: 12).stroke(Palette.accent, lineWidth: 2)
                                                .allowsHitTesting(false).accessibilityIdentifier("mighty-selected-" + node.id)
                                        }
                                    }
                            } else { Color.clear }
                        }
                        .frame(width: node.frame.width, height: node.frame.height)
                        .overlay(alignment: .bottomTrailing) {
                            if !node.isResultFiles {
                                Image(systemName: "arrow.up.left.and.arrow.down.right")
                                    .font(.system(size: 10, weight: .semibold))
                                    .foregroundStyle(selection == node.id ? Palette.accent : .secondary)
                                    .frame(width: 22, height: 22)
                                    .background(Palette.panel.opacity(0.95), in: RoundedRectangle(cornerRadius: 5))
                                    .padding(2)
                                    .help("드래그하여 블록 크기 조절 · 우클릭하여 크기 초기화")
                                    .accessibilityLabel("블록 크기 조절")
                                    .accessibilityIdentifier("mighty-resize-" + node.id)
                                    .contextMenu {
                                        Button("기본 크기로 되돌리기") { onResetSize(node.id) }
                                    }
                            }
                        }
                        .id(node.id)
                        .position(x: MightyGraphCamera.drawnX(nodeX: node.frame.midX, originX: graph.originX), y: node.frame.midY)
                    }
                }
                // The container covers the whole diagram, whose leading edge is
                // negative once a tree is wider than a request card; putting
                // that edge back into the offset leaves the camera, the probe's
                // hit testing and the culling rect all in node coordinates.
                .frame(width: graph.size.width, height: graph.size.height, alignment: .topLeading)
                .scaleEffect(zoom, anchor: .topLeading)
                .offset(x: MightyGraphCamera.drawnOffsetX(cameraX: displayedOffset.x, originX: graph.originX, zoom: zoom), y: displayedOffset.y)
            }
            .frame(width: viewport.size.width, height: viewport.size.height, alignment: .topLeading)
            .clipped()
            .background(Palette.subtle)

            MightyGraphInteraction(sessionID: sessionID, nodes: graph.nodes, zoom: zoom,
                viewportSize: viewport.size, targetToken: scrollTarget?.token,
                targetFrame: targetFrame,
                alignTop: alignTop, selection: $selection, panOffset: panBinding, onResize: onResize, onStranded: onStranded, newestRunID: newestRunID,
                overlay: AnyView(overlay.environment(\.colorScheme, colorScheme)), overlayLayout: overlayLayout, onOverlayResize: onOverlayResize,
                content: diagram.environment(\.colorScheme, colorScheme))
                .frame(width: viewport.size.width, height: viewport.size.height)
            .accessibilityIdentifier("mighty-graph-canvas-\(sessionID)")
            .onChange(of: zoom) { old, new in
                guard old > 0 else { return }
                let center = CGPoint(x: viewport.size.width / 2, y: viewport.size.height / 2)
                let previous = cameraOffset ?? targetFrame.map { MightyGraphLayout.cameraOffset(for: $0, viewport: viewport.size, zoom: old, alignTop: alignTop) } ?? .zero
                cameraOffset = CGPoint(x: center.x - (center.x - previous.x) * new / old,
                                       y: center.y - (center.y - previous.y) * new / old)
            }
        }
    }
}


/// A short request occupies its natural height; long requests stay selectable
/// and scroll within the pinned request area, separate from agent output.
private struct MightyGraphInputPreview: View {
    let input: String
    let width: CGFloat
    let identifier: String
    @ViewState private var measuredHeight: CGFloat = 14
    var body: some View {
        // The native view scrolls within the pinned height on its own, and its
        // selection survives the canvas monitor moving first responder away.
        // The identifier goes on the text view itself, so the accessibility
        // lookups in MightyGraphDiagnostics land on the element that carries
        // the request text as its value.
        MightyGraphSelectableText(text: input, width: max(1, width - 16), fontSize: 11, identifier: identifier,
                                  accessibilityLabel: "요청 내용", onHeight: { height in
            if height.isFinite, height > 0, abs(measuredHeight - height) > 0.5 { measuredHeight = ceil(height) }
        })
        .frame(width: width, height: min(64, max(14, measuredHeight)))
    }
}

/// The next-request draft. Four lines at most, but four lines of *this* text:
/// the preview asks the layout manager how tall its own first four fragments
/// came out, so a Hangul or emoji fallback is reserved for as it is drawn
/// rather than estimated from the Latin system font.
private struct MightyGraphDraftPreview: View {
    let draft: String
    let width: CGFloat
    let identifier: String
    @ViewState private var measuredHeight: CGFloat = 16
    var body: some View {
        MightyGraphSelectableText(text: draft.isEmpty ? "아래 입력창에서 요청을 작성하세요." : draft,
                                  width: width, fontSize: 12, secondary: draft.isEmpty, maximumLines: 4,
                                  identifier: identifier,
                                  accessibilityLabel: draft.isEmpty ? "다음 요청 입력 대기" : nil,
                                  onHeight: { height in
            if height.isFinite, height > 0, abs(measuredHeight - height) > 0.5 { measuredHeight = ceil(height) }
        })
        .frame(maxWidth: .infinity, minHeight: measuredHeight, maxHeight: measuredHeight, alignment: .leading)
    }
}



private struct MightyGraphScrollTarget {
    let token: String
    let nodeID: String
    let alignTop: Bool
}
