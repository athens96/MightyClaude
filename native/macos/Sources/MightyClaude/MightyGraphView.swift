import AppKit
import MightyCore
import SwiftUI

/// Each turn remains a separate tree. Coordinates are computed from complete
/// subtree bounds, so expanding one card moves every following row with it.
struct MightyGraphLayout {
    enum Content: Hashable {
        case request(Int), agent(Int, Int), result(Int), resultFiles(Int), draft
    }
    struct Node: Identifiable {
        let id: String
        let content: Content
        var frame: CGRect
        var isResultFiles: Bool { if case .resultFiles = content { return true }; return false }
    }
    struct Edge: Identifiable {
        let source: String
        let target: String
        var joins: Bool = false
        var id: String { source + "→" + target }
    }
    var nodes: [Node] = []
    var edges: [Edge] = []
    var size: CGSize = .zero
    static let siblingGap: CGFloat = 32
    static let rowGap: CGFloat = 52

    /// Used by the initial SwiftUI render as well as native camera admission.
    /// Computing this before mounting avoids painting the origin then jumping.
    static func cameraOffset(for frame: CGRect, viewport: CGSize, zoom: CGFloat, alignTop: Bool) -> CGPoint {
        CGPoint(x: (viewport.width - frame.width * zoom) / 2 - frame.minX * zoom,
                y: (alignTop ? 16 : max(16, (viewport.height - frame.height * zoom) / 2)) - frame.minY * zoom)
    }

    func route(_ edge: Edge) -> [CGPoint] {
        guard let source = nodes.first(where: { $0.id == edge.source })?.frame,
              let target = nodes.first(where: { $0.id == edge.target })?.frame else { return [] }
        let start = CGPoint(x: source.midX, y: source.maxY)
        let end = CGPoint(x: target.midX, y: target.minY)
        let clearance = min(26, max(0, (end.y - start.y) / 2))
        let middle = edge.joins ? end.y - clearance : start.y + clearance
        return [start, CGPoint(x: start.x, y: middle), CGPoint(x: end.x, y: middle), end]
    }

    static func terminal(_ state: String) -> Bool {
        ["completed", "error", "failed", "stopped", "cancelled", "interrupted"].contains(state)
    }
    static func finished(_ run: MightyGraphRun) -> Bool {
        terminal(run.status) && run.agents.allSatisfy { terminal($0.status) }
    }
    static func nodeID(_ run: MightyGraphRun, suffix: String) -> String {
        MightyGraphBlockSize.nodeID(runID: run.id, suffix: suffix)
    }

    static func make(runs: [MightyGraphRun], draft: String, running: Bool, expanded: Set<String>, blockSizes: [String: MightyGraphBlockSize] = [:], resultFilesRunID: String? = nil) -> Self {
        func size(_ id: String, width: CGFloat, height: CGFloat) -> CGSize {
            guard let custom = blockSizes[id]?.normalized else { return CGSize(width: width, height: height) }
            return CGSize(width: custom.width, height: custom.height)
        }
        struct Tree {
            var nodes: [Node]
            var edges: [Edge]
            var width: CGFloat
            var height: CGFloat
            var top: String
            var leaves: [String]
            mutating func offset(x: CGFloat, y: CGFloat) {
                for index in nodes.indices { nodes[index].frame = nodes[index].frame.offsetBy(dx: x, dy: y) }
            }
        }
        var trees: [Tree] = []
        for (runIndex, run) in runs.enumerated() {
            let mainID = nodeID(run, suffix: "request")
            let mainSize = size(mainID, width: 500, height: expanded.contains(mainID) ? 540 : 280)
            let mainHeight = mainSize.height
            let resultID = nodeID(run, suffix: "result")
            let resultSize = size(resultID, width: 500, height: expanded.contains(resultID) ? 440 : 200)
            var visited = Set<Int>()
            let indexes = Dictionary(run.agents.enumerated().map { ($0.element.id, $0.offset) }, uniquingKeysWith: { first, _ in first })
            func branch(_ index: Int, depth: Int = 0) -> Tree? {
                guard depth < 64, visited.insert(index).inserted else { return nil }
                let agent = run.agents[index]
                let id = nodeID(run, suffix: "agent:\(agent.id)")
                let cardSize = size(id, width: 360, height: expanded.contains(id) ? 480 : 240)
                let height = cardSize.height
                let children = run.agents.indices.filter { $0 != index && run.agents[$0].parentID == agent.id }
                    .compactMap { branch($0, depth: depth + 1) }
                let childrenWidth = children.reduce(CGFloat(0)) { $0 + $1.width } + CGFloat(max(0, children.count - 1)) * siblingGap
                let width = max(cardSize.width, childrenWidth)
                var result = Tree(nodes: [Node(id: id, content: .agent(runIndex, index), frame: CGRect(x: (width - cardSize.width) / 2, y: 0, width: cardSize.width, height: height))], edges: [], width: width, height: height, top: id, leaves: [id])
                if !children.isEmpty {
                    result.leaves = []
                    var x = (width - childrenWidth) / 2
                    for var child in children {
                        child.offset(x: x, y: height + rowGap)
                        result.nodes += child.nodes
                        result.edges += [Edge(source: id, target: child.top)] + child.edges
                        result.leaves += child.leaves
                        result.height = max(result.height, height + rowGap + child.height)
                        x += child.width + siblingGap
                    }
                }
                return result
            }
            var branches: [Tree] = []
            for index in run.agents.indices {
                let parent = run.agents[index].parentID
                if parent == nil || indexes[parent!] == nil || parent == run.agents[index].id {
                    if let tree = branch(index) { branches.append(tree) }
                }
            }
            // Malformed/cyclic legacy data still remains visible, once per agent.
            for index in run.agents.indices where !visited.contains(index) {
                if let tree = branch(index) { branches.append(tree) }
            }
            let branchWidth = branches.reduce(CGFloat(0)) { $0 + $1.width } + CGFloat(max(0, branches.count - 1)) * siblingGap
            let width = max(mainSize.width, branchWidth, finished(run) ? resultSize.width : 0)
            var tree = Tree(nodes: [Node(id: mainID, content: .request(runIndex), frame: CGRect(x: (width - mainSize.width) / 2, y: 0, width: mainSize.width, height: mainHeight))], edges: [], width: width, height: mainHeight, top: mainID, leaves: [mainID])
            if !branches.isEmpty {
                tree.leaves = []
                var x = (width - branchWidth) / 2
                for var child in branches {
                    child.offset(x: x, y: mainHeight + rowGap)
                    tree.nodes += child.nodes
                    tree.edges += [Edge(source: mainID, target: child.top)] + child.edges
                    tree.leaves += child.leaves
                    tree.height = max(tree.height, mainHeight + rowGap + child.height)
                    x += child.width + siblingGap
                }
            }
            if finished(run) {
                let id = resultID
                let height = resultSize.height
                tree.nodes.append(Node(id: id, content: .result(runIndex), frame: CGRect(x: (width - resultSize.width) / 2, y: tree.height + rowGap, width: resultSize.width, height: height)))
                tree.edges += tree.leaves.map { Edge(source: $0, target: id, joins: true) }
                tree.leaves = [id]
                tree.height += rowGap + height
            }
            trees.append(tree)
        }
        let hasPending = (!running && runs.last.map(finished) != false) || !draft.isEmpty
        if hasPending {
            let pendingSize = size("pending-input", width: 500, height: 140)
            trees.append(Tree(nodes: [Node(id: "pending-input", content: .draft, frame: CGRect(origin: .zero, size: pendingSize))], edges: [], width: pendingSize.width, height: pendingSize.height, top: "pending-input", leaves: ["pending-input"]))
        }
        let width = max(500, trees.map(\.width).max() ?? 500)
        var result = Self()
        var y: CGFloat = 24
        var previous: [String] = []
        for var tree in trees {
            tree.offset(x: 24 + (width - tree.width) / 2, y: y)
            result.nodes += tree.nodes
            result.edges += previous.map { Edge(source: $0, target: tree.top, joins: true) } + tree.edges
            previous = tree.leaves
            y += tree.height + rowGap
        }
        result.size = CGSize(width: width + 48, height: max(188, y - rowGap + 24))
        if let resultFilesRunID, let runIndex = runs.firstIndex(where: { $0.id == resultFilesRunID }),
           runs[runIndex].status == "completed", finished(runs[runIndex]),
           let resultNode = result.nodes.first(where: { $0.content == .result(runIndex) }) {
            let panelID = nodeID(runs[runIndex], suffix: "result-files")
            let panelSize = CGSize(width: 320, height: resultNode.frame.height)
            let frame = CGRect(x: resultNode.frame.maxX + 16, y: resultNode.frame.minY, width: panelSize.width, height: panelSize.height)
            // This is an attachment to the result, never a flow edge or a new
            // centerline. Only the trailing canvas extent grows horizontally.
            result.nodes.append(Node(id: panelID, content: .resultFiles(runIndex), frame: frame))
            result.size.width = max(result.size.width, frame.maxX + 24)
        }
        return result
    }
}

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
    /// The pane's guided Mighty style; nil for the plain CLI style.
    var style: String? = nil
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
    @StateObject private var resultFiles = MightyGraphResultFilesModel()

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

    private var layout: MightyGraphLayout { .make(runs: runs, draft: draft, running: running, expanded: expanded, blockSizes: blockSizes.merging(resized) { _, new in new }, resultFilesRunID: resultFiles.selectedRunID) }

    var body: some View {
        let graph = layout
        let agentCount = runs.reduce(0) { $0 + $1.agents.filter { !$0.isTask && !$0.isSteer && !$0.isCompact && !$0.isQuestion }.count }
        let questionCount = runs.reduce(0) { $0 + $1.agents.filter(\.isQuestion).count }
        let taskCount = runs.reduce(0) { $0 + $1.agents.filter(\.isTask).count }
        let steerCount = runs.reduce(0) { $0 + $1.agents.filter(\.isSteer).count }
        let compactCount = runs.reduce(0) { $0 + $1.agents.filter(\.isCompact).count }
        let tokens = runs.reduce(GraphTokenUsage()) { $0 + ($1.totalUsage ?? GraphTokenUsage()) }
        VStack(spacing: 0) {
            HStack(spacing: 10) {
                Label("마이티", systemImage: "point.3.connected.trianglepath.dotted")
                    .font(.system(size: 12, weight: .semibold))
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
                              scrollTarget: scrollTarget ?? MightyGraphScrollTarget(token: "initial:" + sessionID, nodeID: initialTarget(graph), alignTop: false), selection: $selectedNodeID, edges: graphEdges(graph),
                              overlay: reference.map { AnyView(referenceOverlay($0)) } ?? AnyView(EmptyView()),
                              overlayLayout: reference == nil ? nil : MightyOverlayLayout(onLeft: referenceOnLeft, storedWidth: bubbleWidth, storedHeight: bubbleHeight),
                              onOverlayResize: { size, _ in
                                  // .zero is the corner's double click: back to the default size.
                                  if size == .zero { bubbleWidth = MightyGraphReferenceBubble.defaultWidth; bubbleHeight = 0 }
                                  else { bubbleWidth = Double(size.width); bubbleHeight = Double(size.height) }
                              },
                              onResize: resize, onResetSize: resetSize, card: card)
                .onChange(of: runs.last?.id) { _, _ in
                    guard let last = runs.last else { return }
                    scrollTarget = MightyGraphScrollTarget(token: "run:" + last.id, nodeID: MightyGraphLayout.nodeID(last, suffix: "request"), alignTop: true)
                }
        }
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("mighty-graph-\(sessionID)")
        .task(id: MightyGraphResultFilesModel.Request(sessionID: sessionID, runs: runs, root: workspaceRoot)) {
            await resultFiles.load(.init(sessionID: sessionID, runs: runs, root: workspaceRoot))
        }
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
        if finished { onSaveBlockSize(id, value) }
    }

    private func resetSize(_ id: String) {
        resized.removeValue(forKey: id)
        expanded.remove(id)
        onSaveBlockSize(id, nil)
    }

    private func initialTarget(_ graph: MightyGraphLayout) -> String {
        if graph.nodes.contains(where: { $0.content == .draft }) { return "pending-input" }
        return runs.last.map { MightyGraphLayout.nodeID($0, suffix: "request") } ?? "pending-input"
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
            transcriptCard(node, title: (MightyStyles.requestTitle(forInput: run.input, style: style).map { $0 + " · " } ?? "") + "요청 \(index + 1) · \(ProviderOptions.label(provider))", icon: "arrow.up.message", status: run.status,
                           input: run.input, entries: run.rootEntries, tint: Palette.accent, usage: run.usage)
        case .agent(let runIndex, let agentIndex):
            let agent = runs[runIndex].agents[agentIndex]
            let look = Self.agentPresentation(agent)
            transcriptCard(node, title: look.title, icon: look.icon, status: agent.status, input: agent.input, entries: agent.entries, tint: look.tint, usage: agent.usage)
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
                                usage: GraphTokenUsage? = nil, usageLabel: String = "이 블록", resultFilesRunID: String? = nil) -> some View {
        let content = entries.filter { $0.kind != "user" }
        return VStack(spacing: 0) {
            HStack(spacing: 7) {
                Image(systemName: icon).foregroundStyle(tint)
                Text(title).font(.system(size: 12, weight: .semibold)).lineLimit(1).help(title)
                Spacer(minLength: 3)
                if selectedNodeID == node.id { blockScrollLabel(node.id) }
                MightyGraphActivityIndicator(status: status, tint: tint)
                Text(statusLabel(status)).font(.system(size: 10)).foregroundStyle(.secondary)
                if let usage, !usage.isEmpty {
                    Text(usage.summary).font(.system(size: 10, design: .monospaced)).foregroundStyle(.secondary).lineLimit(1)
                        .padding(.horizontal, 6).padding(.vertical, 2).background(Palette.subtle, in: Capsule())
                        .help(usageLabel + " · " + usage.detail)
                        .accessibilityLabel(usageLabel + " " + usage.detail)
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
                Button {
                    resized.removeValue(forKey: node.id)
                    onSaveBlockSize(node.id, nil)
                    if !expanded.insert(node.id).inserted { expanded.remove(node.id) }
                } label: { Image(systemName: expanded.contains(node.id) ? "rectangle.compress.vertical" : "rectangle.expand.vertical") }
                    .buttonStyle(.plain).help(expanded.contains(node.id) ? "내용 접기" : "내용 더 보기")
                    .accessibilityLabel(expanded.contains(node.id) ? "내용 접기" : "내용 더 보기")
                    .accessibilityIdentifier("mighty-expand-\(node.id)")
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
                    onReference: workspaceRoot == nil ? nil : { path, line in openReference(path, line: line) })
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
    @Binding var selection: String?
    let edges: Edges
    var overlay: AnyView = AnyView(EmptyView())
    var overlayLayout: MightyOverlayLayout? = nil
    var onOverlayResize: (CGSize, Bool) -> Void = { _, _ in }
    let onResize: (String, CGSize, Bool) -> Void
    let onResetSize: (String) -> Void
    let card: (MightyGraphLayout.Node) -> Card
    @ViewState private var cameraOffset: CGPoint?
    @Environment(\.colorScheme) private var colorScheme

    var body: some View {
        GeometryReader { viewport in
            let targetFrame = graph.nodes.first(where: { $0.id == scrollTarget?.nodeID })?.frame
            let initialOffset = targetFrame.map { MightyGraphLayout.cameraOffset(for: $0, viewport: viewport.size, zoom: zoom, alignTop: scrollTarget?.alignTop ?? false) } ?? .zero
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
                        .position(x: node.frame.midX, y: node.frame.midY)
                    }
                }
                .frame(width: graph.size.width, height: graph.size.height, alignment: .topLeading)
                .scaleEffect(zoom, anchor: .topLeading)
                .offset(x: displayedOffset.x, y: displayedOffset.y)
            }
            .frame(width: viewport.size.width, height: viewport.size.height, alignment: .topLeading)
            .clipped()
            .background(Palette.subtle)

            MightyGraphInteraction(sessionID: sessionID, nodes: graph.nodes, zoom: zoom,
                viewportSize: viewport.size, targetToken: scrollTarget?.token,
                targetFrame: targetFrame,
                alignTop: scrollTarget?.alignTop ?? false, selection: $selection, panOffset: panBinding, onResize: onResize,
                overlay: AnyView(overlay.environment(\.colorScheme, colorScheme)), overlayLayout: overlayLayout, onOverlayResize: onOverlayResize,
                content: diagram.environment(\.colorScheme, colorScheme))
                .frame(width: viewport.size.width, height: viewport.size.height)
            .accessibilityIdentifier("mighty-graph-canvas-\(sessionID)")
            .onChange(of: zoom) { old, new in
                guard old > 0 else { return }
                let center = CGPoint(x: viewport.size.width / 2, y: viewport.size.height / 2)
                let previous = cameraOffset ?? targetFrame.map { MightyGraphLayout.cameraOffset(for: $0, viewport: viewport.size, zoom: old, alignTop: scrollTarget?.alignTop ?? false) } ?? .zero
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
