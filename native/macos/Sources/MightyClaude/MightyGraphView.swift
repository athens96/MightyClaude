import AppKit
import MightyCore
import SwiftUI

/// Each turn remains a separate tree. Coordinates are computed from complete
/// subtree bounds, so expanding one card moves every following row with it.
struct MightyGraphLayout {
    enum Content: Hashable {
        case request(Int), agent(Int, Int), result(Int), draft
    }
    struct Node: Identifiable {
        let id: String
        let content: Content
        var frame: CGRect
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
        "\(run.id.utf8.count):\(run.id):\(suffix)"
    }

    static func make(runs: [MightyGraphRun], draft: String, running: Bool, expanded: Set<String>) -> Self {
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
            let mainHeight: CGFloat = expanded.contains(mainID) ? 540 : 280
            var visited = Set<Int>()
            let indexes = Dictionary(run.agents.enumerated().map { ($0.element.id, $0.offset) }, uniquingKeysWith: { first, _ in first })
            func branch(_ index: Int, depth: Int = 0) -> Tree? {
                guard depth < 64, visited.insert(index).inserted else { return nil }
                let agent = run.agents[index]
                let id = nodeID(run, suffix: "agent:\(agent.id)")
                let height: CGFloat = expanded.contains(id) ? 480 : 240
                let children = run.agents.indices.filter { $0 != index && run.agents[$0].parentID == agent.id }
                    .compactMap { branch($0, depth: depth + 1) }
                let childrenWidth = children.reduce(CGFloat(0)) { $0 + $1.width } + CGFloat(max(0, children.count - 1)) * siblingGap
                let width = max(360, childrenWidth)
                var result = Tree(nodes: [Node(id: id, content: .agent(runIndex, index), frame: CGRect(x: (width - 360) / 2, y: 0, width: 360, height: height))], edges: [], width: width, height: height, top: id, leaves: [id])
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
            let width = max(500, branchWidth)
            var tree = Tree(nodes: [Node(id: mainID, content: .request(runIndex), frame: CGRect(x: (width - 500) / 2, y: 0, width: 500, height: mainHeight))], edges: [], width: width, height: mainHeight, top: mainID, leaves: [mainID])
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
                let id = nodeID(run, suffix: "result")
                let height: CGFloat = expanded.contains(id) ? 440 : 200
                tree.nodes.append(Node(id: id, content: .result(runIndex), frame: CGRect(x: (width - 500) / 2, y: tree.height + rowGap, width: 500, height: height)))
                tree.edges += tree.leaves.map { Edge(source: $0, target: id, joins: true) }
                tree.leaves = [id]
                tree.height += rowGap + height
            }
            trees.append(tree)
        }
        let hasPending = (!running && runs.last.map(finished) != false) || !draft.isEmpty
        if hasPending {
            trees.append(Tree(nodes: [Node(id: "pending-input", content: .draft, frame: CGRect(x: 0, y: 0, width: 500, height: 140))], edges: [], width: 500, height: 140, top: "pending-input", leaves: ["pending-input"]))
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
        return result
    }
}

struct MightyGraphView: View {
    let sessionID: String
    let provider: String
    let runs: [MightyGraphRun]
    let draft: String
    let running: Bool
    let onFocus: () -> Void
    @ViewState private var expanded = Set<String>()
    @ViewState private var zoom: CGFloat = 1
    @ViewState private var scrollTarget: MightyGraphScrollTarget?
    @ViewState private var selectedNodeID: String?

    private var layout: MightyGraphLayout { .make(runs: runs, draft: draft, running: running, expanded: expanded) }

    var body: some View {
        let graph = layout
        VStack(spacing: 0) {
            HStack(spacing: 10) {
                Label("마이티", systemImage: "point.3.connected.trianglepath.dotted")
                    .font(.system(size: 12, weight: .semibold))
                Text("요청 \(runs.count) · 하위 에이전트 \(runs.reduce(0) { $0 + $1.agents.count })")
                    .font(.system(size: 10)).foregroundStyle(.secondary).lineLimit(1)
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
            MightyGraphCanvas(graph: graph, zoom: zoom, sessionID: sessionID, scrollTarget: scrollTarget, selection: $selectedNodeID, edges: graphEdges(graph), card: card)
                .onAppear {
                    guard scrollTarget == nil else { return }
                    scrollTarget = MightyGraphScrollTarget(token: "initial:" + sessionID, nodeID: initialTarget(graph), alignTop: false)
                }
                .onChange(of: runs.last?.id) { _, _ in
                    guard let last = runs.last else { return }
                    scrollTarget = MightyGraphScrollTarget(token: "run:" + last.id, nodeID: MightyGraphLayout.nodeID(last, suffix: "request"), alignTop: true)
                }
        }
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("mighty-graph-\(sessionID)")
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
                Text(draft.isEmpty ? "아래 입력창에서 요청을 작성하세요." : draft)
                    .font(.system(size: 12)).foregroundStyle(draft.isEmpty ? .secondary : .primary)
                    .lineLimit(4).textSelection(.enabled).frame(maxWidth: .infinity, alignment: .leading)
                    .accessibilityIdentifier("mighty-draft-\(sessionID)")
                Spacer(minLength: 0)
            }
            .padding(16).background(Palette.panel, in: RoundedRectangle(cornerRadius: 12))
            .overlay { RoundedRectangle(cornerRadius: 12).stroke(Palette.accent.opacity(0.5), style: StrokeStyle(lineWidth: 1, dash: [5, 4])) }
            .accessibilityElement(children: .contain).accessibilityIdentifier("mighty-node-\(node.id)")
        case .request(let index):
            let run = runs[index]
            transcriptCard(node, title: "요청 \(index + 1) · \(ProviderOptions.label(provider))", icon: "arrow.up.message", status: run.status,
                           input: run.input, entries: run.rootEntries, tint: Palette.accent)
        case .agent(let runIndex, let agentIndex):
            let agent = runs[runIndex].agents[agentIndex]
            transcriptCard(node, title: agent.title.isEmpty ? "하위 에이전트" : agent.title, icon: "person.crop.square.filled.and.at.rectangle", status: agent.status,
                           input: agent.input, entries: agent.entries, tint: .purple)
        case .result(let index):
            let run = runs[index]
            let failed = ["error", "failed"].contains(run.status)
            let stopped = ["stopped", "cancelled", "interrupted"].contains(run.status)
            let status = failed ? "error" : stopped ? "stopped" : "completed"
            transcriptCard(node, title: failed ? "요청 실패" : stopped ? "요청 중단" : "최종 결과", icon: failed ? "exclamationmark.triangle" : stopped ? "stop.circle" : "checkmark.seal",
                           status: status, input: "", entries: run.resultEntries, tint: failed ? .red : stopped ? .orange : .green)
        }
    }

    private func transcriptCard(_ node: MightyGraphLayout.Node, title: String, icon: String, status: String, input: String, entries: [LogEntry], tint: Color) -> some View {
        let content = entries.filter { $0.kind != "user" }
        return VStack(spacing: 0) {
            HStack(spacing: 7) {
                Image(systemName: icon).foregroundStyle(tint)
                Text(title).font(.system(size: 12, weight: .semibold)).lineLimit(1).help(title)
                Spacer(minLength: 3)
                if selectedNodeID == node.id { blockScrollLabel(node.id) }
                MightyGraphActivityIndicator(status: status, tint: tint)
                Text(statusLabel(status)).font(.system(size: 10)).foregroundStyle(.secondary)
                Button {
                    if !expanded.insert(node.id).inserted { expanded.remove(node.id) }
                } label: { Image(systemName: expanded.contains(node.id) ? "arrow.down.right.and.arrow.up.left" : "arrow.up.left.and.arrow.down.right") }
                    .buttonStyle(.plain).help(expanded.contains(node.id) ? "내용 접기" : "내용 더 보기")
                    .accessibilityLabel(expanded.contains(node.id) ? "내용 접기" : "내용 더 보기")
                    .accessibilityIdentifier("mighty-expand-\(node.id)")
            }.padding(.horizontal, 12).frame(height: 38)
            Divider()
            if !input.isEmpty {
                MightyGraphInputPreview(input: input, width: node.frame.width - 24)
                    .padding(.horizontal, 12).padding(.vertical, 8)
                    .background(tint.opacity(0.055))
                    .accessibilityIdentifier("mighty-request-\(node.id)")
                Divider()
            }
            if content.isEmpty {
                Text(MightyGraphLayout.terminal(status) ? "별도의 응답 내용이 없습니다." : "에이전트 응답을 기다리고 있습니다…")
                    .font(.system(size: 12)).foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading).padding(15)
            } else {
                AgentTranscriptView(sessionId: "graph-\(sessionID)-\(node.id)", provider: provider,
                    running: !MightyGraphLayout.terminal(status), entries: content, onFocus: onFocus)
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
    let card: (MightyGraphLayout.Node) -> Card
    @ViewState private var cameraOffset: CGPoint = .zero
    @Environment(\.colorScheme) private var colorScheme

    var body: some View {
        GeometryReader { viewport in
            let visible = CGRect(x: floor(-cameraOffset.x / zoom / 64) * 64 - 192,
                                 y: floor(-cameraOffset.y / zoom / 64) * 64 - 192,
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
                        .id(node.id)
                        .position(x: node.frame.midX, y: node.frame.midY)
                    }
                }
                .frame(width: graph.size.width, height: graph.size.height, alignment: .topLeading)
                .scaleEffect(zoom, anchor: .topLeading)
                .offset(x: cameraOffset.x, y: cameraOffset.y)
            }
            .frame(width: viewport.size.width, height: viewport.size.height, alignment: .topLeading)
            .clipped()
            .background(Palette.subtle)

            MightyGraphInteraction(sessionID: sessionID, nodes: graph.nodes, zoom: zoom,
                viewportSize: viewport.size, targetToken: scrollTarget?.token,
                targetFrame: graph.nodes.first(where: { $0.id == scrollTarget?.nodeID })?.frame,
                alignTop: scrollTarget?.alignTop ?? false, selection: $selection, panOffset: $cameraOffset,
                content: diagram.environment(\.colorScheme, colorScheme))
                .frame(width: viewport.size.width, height: viewport.size.height)
            .accessibilityIdentifier("mighty-graph-canvas-\(sessionID)")
            .onChange(of: zoom) { old, new in
                guard old > 0 else { return }
                let center = CGPoint(x: viewport.size.width / 2, y: viewport.size.height / 2)
                cameraOffset = CGPoint(x: center.x - (center.x - cameraOffset.x) * new / old,
                                       y: center.y - (center.y - cameraOffset.y) * new / old)
            }
        }
    }
}


/// A short request occupies its natural height; long requests stay selectable
/// and scroll within the pinned request area, separate from agent output.
private struct MightyGraphInputPreview: View {
    let input: String
    let width: CGFloat
    @ViewState private var measuredHeight: CGFloat = 14
    var body: some View {
        ScrollView(.vertical) {
            Text(input).font(.system(size: 11)).textSelection(.enabled)
                .frame(width: max(1, width - 16), alignment: .leading)
                .fixedSize(horizontal: false, vertical: true)
                .background(GeometryReader { geometry in
                    Color.clear.preference(key: MightyGraphInputHeight.self, value: geometry.size.height)
                })
                .padding(.trailing, 16)
        }
        .frame(width: width, height: min(64, max(14, measuredHeight)))
        .onPreferenceChange(MightyGraphInputHeight.self) { height in
            if height.isFinite, height > 0, abs(measuredHeight - height) > 0.5 { measuredHeight = ceil(height) }
        }
    }
}
private struct MightyGraphInputHeight: PreferenceKey {
    static let defaultValue: CGFloat = 0
    static func reduce(value: inout CGFloat, nextValue: () -> CGFloat) { value = max(value, nextValue()) }
}


private struct MightyGraphScrollTarget {
    let token: String
    let nodeID: String
    let alignTop: Bool
}
