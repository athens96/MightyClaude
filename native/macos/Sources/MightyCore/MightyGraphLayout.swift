import CoreGraphics
import Foundation

/// Each turn remains a separate tree. Coordinates are computed from complete
/// subtree bounds, so expanding one card moves every following row with it.
public struct MightyGraphLayout {
    public enum Content: Hashable {
        case request(Int), agent(Int, Int), result(Int), resultFiles(Int), draft
    }
    public struct Node: Identifiable {
        public let id: String
        public let content: Content
        public var frame: CGRect
        public var isResultFiles: Bool { if case .resultFiles = content { return true }; return false }
    }
    public struct Edge: Identifiable {
        public let source: String
        public let target: String
        public var joins: Bool = false
        public var id: String { source + "→" + target }
    }
    public var nodes: [Node] = []
    public var edges: [Edge] = []
    public var size: CGSize = .zero
    /// Node coordinates keep a fixed origin, so a wide tree reaches into
    /// negative x rather than pushing the diagram. The drawn container starts
    /// here instead of at 0; the camera still works in node coordinates.
    public var originX: CGFloat = 0
    /// The node id of the latest result card that is currently auto-fitting to
    /// the viewport. nil when no viewport is supplied, when a shared result size
    /// overrides the fit, or when there is no finished run yet.
    public var fittedResultID: String? = nil
    static let siblingGap: CGFloat = 32
    static let rowGap: CGFloat = 52

    /// Used by the initial SwiftUI render as well as native camera admission.
    /// Computing this before mounting avoids painting the origin then jumping.
    public static func cameraOffset(for frame: CGRect, viewport: CGSize, zoom: CGFloat, alignTop: Bool) -> CGPoint {
        CGPoint(x: (viewport.width - frame.width * zoom) / 2 - frame.minX * zoom,
                y: (alignTop ? 16 : max(16, (viewport.height - frame.height * zoom) / 2)) - frame.minY * zoom)
    }

    public func route(_ edge: Edge) -> [CGPoint] {
        guard let source = nodes.first(where: { $0.id == edge.source })?.frame,
              let target = nodes.first(where: { $0.id == edge.target })?.frame else { return [] }
        let start = CGPoint(x: source.midX, y: source.maxY)
        let end = CGPoint(x: target.midX, y: target.minY)
        let clearance = min(26, max(0, (end.y - start.y) / 2))
        let middle = edge.joins ? end.y - clearance : start.y + clearance
        return [start, CGPoint(x: start.x, y: middle), CGPoint(x: end.x, y: middle), end]
    }

    public static func terminal(_ state: String) -> Bool {
        ["completed", "error", "failed", "stopped", "cancelled", "interrupted"].contains(state)
    }
    public static func finished(_ run: MightyGraphRun) -> Bool {
        terminal(run.status) && run.agents.allSatisfy { terminal($0.status) }
    }
    public static func nodeID(_ run: MightyGraphRun, suffix: String) -> String {
        MightyGraphBlockSize.nodeID(runID: run.id, suffix: suffix)
    }

    public static func make(runs: [MightyGraphRun], draft: String, running: Bool, expanded: Set<String>, blockSizes: [String: MightyGraphBlockSize] = [:], resultFilesRunID: String? = nil, viewport: CGSize? = nil, sharedResultSize: MightyGraphBlockSize? = nil) -> Self {
        // The latest result card is the result of the last finished run in the list.
        let latestFinishedRunIndex = runs.indices.last(where: { finished(runs[$0]) })
        let latestResultID = latestFinishedRunIndex.map { nodeID(runs[$0], suffix: "result") }

        // The files panel reduces available width only when it is open for the latest result.
        let filesPanelOpenForLatest: Bool = {
            guard let idx = latestFinishedRunIndex,
                  let panelRunID = resultFilesRunID,
                  runs[idx].id == panelRunID,
                  runs[idx].status == "completed" else { return false }
            return true
        }()

        // Auto-fit size for the latest result card (no .normalized clamp applied).
        let autoFitResultSize: CGSize? = {
            guard let vp = viewport, latestResultID != nil else { return nil }
            let filesOffset: CGFloat = filesPanelOpenForLatest ? 336.0 : 0.0
            return CGSize(width: max(500, vp.width - 48 - filesOffset),
                          height: max(200, vp.height - 48))
        }()

        func size(_ id: String, width: CGFloat, height: CGFloat) -> CGSize {
            // Latest result card: use shared or auto-fit size when viewport is active.
            if let latestID = latestResultID, id == latestID, viewport != nil {
                if let shared = sharedResultSize { return CGSize(width: shared.width, height: shared.height) }
                if let autoFit = autoFitResultSize { return autoFit }
            }
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
            let mainSize = size(mainID, width: MightyGraphCamera.requestWidth, height: expanded.contains(mainID) ? 540 : 280)
            let mainHeight = mainSize.height
            let resultID = nodeID(run, suffix: "result")
            let resultSize = size(resultID, width: MightyGraphCamera.requestWidth, height: expanded.contains(resultID) ? 440 : 200)
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
            let pendingID = MightyGraphCamera.pendingNodeID
            let pendingSize = size(pendingID, width: MightyGraphCamera.requestWidth, height: 140)
            trees.append(Tree(nodes: [Node(id: pendingID, content: .draft, frame: CGRect(origin: .zero, size: pendingSize))], edges: [], width: pendingSize.width, height: pendingSize.height, top: pendingID, leaves: [pendingID]))
        }
        var result = Self()
        var y: CGFloat = 24
        var previous: [String] = []
        for var tree in trees {
            // One fixed centreline for every tree. A tree's own width decides
            // how far it reaches to each side and nothing else moves, so a new
            // branch never slides the request and result cards already placed.
            tree.offset(x: MightyGraphCamera.x(for: tree.width), y: y)
            result.nodes += tree.nodes
            result.edges += previous.map { Edge(source: $0, target: tree.top, joins: true) } + tree.edges
            previous = tree.leaves
            y += tree.height + rowGap
        }
        let leading = result.nodes.map(\.frame.minX).min() ?? MightyGraphCamera.x(for: MightyGraphCamera.requestWidth)
        let trailing = result.nodes.map(\.frame.maxX).max() ?? (MightyGraphCamera.centreX + MightyGraphCamera.requestWidth / 2)
        result.originX = MightyGraphCamera.originX(leadingMinX: leading)
        result.size = CGSize(width: MightyGraphCamera.canvasWidth(leading: leading, trailing: trailing), height: max(188, y - rowGap + 24))
        if let resultFilesRunID, let runIndex = runs.firstIndex(where: { $0.id == resultFilesRunID }),
           runs[runIndex].status == "completed", finished(runs[runIndex]),
           let resultNode = result.nodes.first(where: { $0.content == .result(runIndex) }) {
            let panelID = nodeID(runs[runIndex], suffix: "result-files")
            let panelSize = CGSize(width: 320, height: resultNode.frame.height)
            let frame = CGRect(x: resultNode.frame.maxX + 16, y: resultNode.frame.minY, width: panelSize.width, height: panelSize.height)
            // This is an attachment to the result, never a flow edge or a new
            // centerline. Only the trailing canvas extent grows horizontally.
            result.nodes.append(Node(id: panelID, content: .resultFiles(runIndex), frame: frame))
            result.size.width = max(result.size.width, frame.maxX + 24 - result.originX)
        }
        result.fittedResultID = (viewport != nil && sharedResultSize == nil) ? latestResultID : nil
        return result
    }
}
