import CoreGraphics
import Foundation

/// Each turn remains a separate tree. Coordinates are computed from complete
/// subtree bounds, so expanding one card moves every following row with it.
public struct MightyGraphLayout {
    public enum Content: Hashable {
        /// `execution` is a run index and an `OuroborosExecutionLink.key`.
        /// `images` is a run index and the step it attaches to (`MightyGraphImages`).
        case request(Int), agent(Int, Int), result(Int), resultFiles(Int), execution(Int, String), images(Int, String), draft
        /// The top of the diagram: loads older requests from the session record.
        case history
    }
    public struct Node: Identifiable {
        public let id: String
        public let content: Content
        public var frame: CGRect
        public var isResultFiles: Bool { if case .resultFiles = content { return true }; return false }
        /// Attachments beside the flow: not resized, scrolled at once.
        public var isAuxiliary: Bool {
            switch content {
            case .resultFiles, .execution, .images: return true
            default: return false
            }
        }
    }
    /// A background execution's block, attached beside the request that started it.
    public struct Execution: Hashable, Sendable {
        public var runID: String
        public var key: String
        public init(runID: String, key: String) { self.runID = runID; self.key = key }
    }
    public static let executionSuffix = "ouroboros-execution:"
    public static let executionWidth: CGFloat = 380
    static let executionGap: CGFloat = 16
    /// Collapsed, it holds the goal, progress, counts, one phase line, a
    /// three-line note and the footer without clipping.
    public static func executionHeight(expanded: Bool) -> CGFloat { expanded ? 470 : 290 }
    /// A step's picture preview, attached beside the block that produced them.
    public struct ImageGallery: Hashable, Sendable {
        public var runID: String
        /// "request", or "agent:" and the agent's id.
        public var step: String
        public var count: Int
        public init(runID: String, step: String, count: Int) { self.runID = runID; self.step = step; self.count = count }
    }
    public static let imagesSuffix = "images:"
    public static let imagesWidth: CGFloat = 304
    /// Thumbnails per row and on screen: two rows folded, four unfolded; the
    /// last visible tile carries "+k" for the rest.
    public static let imagesColumns = 3
    public static func visibleImages(count: Int, expanded: Bool) -> Int { min(count, imagesColumns * (expanded ? 4 : 2)) }
    public static let imagesTile: CGFloat = 88
    public static func imagesHeight(count: Int, expanded: Bool) -> CGFloat {
        let rows = max(1, (visibleImages(count: count, expanded: expanded) + imagesColumns - 1) / imagesColumns)
        return 38 + 24 + CGFloat(rows) * imagesTile + CGFloat(rows - 1) * 8
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
    /// Older requests loaded from the session record sit above the first
    /// retained one, at negative y, so loading them moves nothing already on
    /// screen. The drawn container starts here, as it does at `originX`.
    public var originY: CGFloat = 0
    /// The node id of the latest result card that is currently auto-fitting to
    /// the viewport. nil when no viewport is supplied, when a shared result size
    /// overrides the fit, or when there is no finished run yet.
    public var fittedResultID: String? = nil
    static let siblingGap: CGFloat = 32
    static let rowGap: CGFloat = 52
    public static let historyNodeID = "history-top"
    public static let historyWidth: CGFloat = 380
    public static let historyHeight: CGFloat = 44

    /// Used by the initial SwiftUI render as well as native camera admission.
    /// Computing this before mounting avoids painting the origin then jumping.
    public static func cameraOffset(for frame: CGRect, viewport: CGSize, zoom: CGFloat, alignTop: Bool) -> CGPoint {
        CGPoint(x: (viewport.width - frame.width * zoom) / 2 - frame.minX * zoom,
                y: (alignTop ? 16 : max(16, (viewport.height - frame.height * zoom) / 2)) - frame.minY * zoom)
    }

    public func route(_ edge: Edge) -> [CGPoint] {
        guard let source = nodes.first(where: { $0.id == edge.source })?.frame,
              let target = nodes.first(where: { $0.id == edge.target })?.frame else { return [] }
        return Self.route(from: source, to: target, joins: edge.joins)
    }
    /// Every edge's route with the nodes looked up once. With `visible`, an
    /// edge whose whole span lies outside it is left out, as offscreen cards are.
    public func routes(in visible: CGRect? = nil) -> [[CGPoint]] {
        let frames = Dictionary(nodes.map { ($0.id, $0.frame) }, uniquingKeysWith: { first, _ in first })
        return edges.compactMap { edge in
            guard let source = frames[edge.source], let target = frames[edge.target] else { return nil }
            if let visible, !visible.intersects(source.union(target)) { return nil }
            return Self.route(from: source, to: target, joins: edge.joins)
        }
    }
    static func route(from source: CGRect, to target: CGRect, joins: Bool) -> [CGPoint] {
        let start = CGPoint(x: source.midX, y: source.maxY)
        let end = CGPoint(x: target.midX, y: target.minY)
        let clearance = min(26, max(0, (end.y - start.y) / 2))
        let middle = joins ? end.y - clearance : start.y + clearance
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

    public static func latestResultID(runs: [MightyGraphRun]) -> String? {
        runs.indices.last(where: { finished(runs[$0]) }).map { nodeID(runs[$0], suffix: "result") }
    }

    public static func fittedResultID(runs: [MightyGraphRun], viewport: CGSize?, sharedResultSize: MightyGraphBlockSize?) -> String? {
        (viewport != nil && sharedResultSize == nil) ? latestResultID(runs: runs) : nil
    }

    /// `retainedStart` is the index of the run that keeps the first tree's
    /// place (the first run the pane retained, or the one loaded history first
    /// attached above); runs before it, loaded from the session record, stack
    /// upward from there. `history` adds the block at the top that loads more.
    public static func make(runs: [MightyGraphRun], draft: String, running: Bool, expanded: Set<String>, blockSizes: [String: MightyGraphBlockSize] = [:], resultFilesRunID: String? = nil, viewport: CGSize? = nil, sharedResultSize: MightyGraphBlockSize? = nil, executions: [Execution] = [], galleries: [ImageGallery] = [], retainedStart: Int = 0, history: Bool = false) -> Self {
        // The latest result card is the result of the last finished run in the list.
        let latestFinishedRunIndex = runs.indices.last(where: { finished(runs[$0]) })
        let latestResultID = Self.latestResultID(runs: runs)

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
        // The first retained tree starts where the first tree always started;
        // the older ones are stacked upward from it.
        let split = min(max(0, retainedStart), runs.count)
        var tops = [CGFloat](repeating: 24, count: trees.count)
        var y: CGFloat = 24
        for index in trees.indices.dropFirst(split) { tops[index] = y; y += trees[index].height + rowGap }
        var above: CGFloat = 24
        for index in trees.indices.prefix(split).reversed() { above -= trees[index].height + rowGap; tops[index] = above }
        var previous: [String] = []
        for (index, var tree) in trees.enumerated() {
            // One fixed centreline for every tree. A tree's own width decides
            // how far it reaches to each side and nothing else moves, so a new
            // branch never slides the request and result cards already placed.
            tree.offset(x: MightyGraphCamera.x(for: tree.width), y: tops[index])
            result.nodes += tree.nodes
            result.edges += previous.map { Edge(source: $0, target: tree.top, joins: true) } + tree.edges
            previous = tree.leaves
        }
        let bottom = split < trees.count ? y - rowGap : 24 - rowGap
        if history {
            let top = result.nodes.map(\.frame.minY).min() ?? 24
            result.nodes.insert(Node(id: historyNodeID, content: .history,
                                     frame: CGRect(x: MightyGraphCamera.centreX - historyWidth / 2, y: top - 28 - historyHeight, width: historyWidth, height: historyHeight)), at: 0)
        }
        let leading = result.nodes.map(\.frame.minX).min() ?? MightyGraphCamera.x(for: MightyGraphCamera.requestWidth)
        let trailing = result.nodes.map(\.frame.maxX).max() ?? (MightyGraphCamera.centreX + MightyGraphCamera.requestWidth / 2)
        result.originX = MightyGraphCamera.originX(leadingMinX: leading)
        result.originY = min(0, (result.nodes.map(\.frame.minY).min() ?? 24) - 24)
        result.size = CGSize(width: MightyGraphCamera.canvasWidth(leading: leading, trailing: trailing),
                             height: max(188, max(bottom, result.nodes.map(\.frame.maxY).max() ?? 0) + 24 - result.originY))
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
        // A picture preview hangs off the top of the step that produced it,
        // right of every card it would share rows with: an attachment, like
        // the file list — no edge, no centreline, nothing already placed moves.
        for gallery in galleries where gallery.count > 0 {
            guard let runIndex = runs.firstIndex(where: { $0.id == gallery.runID }) else { continue }
            let stepID = nodeID(runs[runIndex], suffix: gallery.step)
            guard let step = result.nodes.first(where: { $0.id == stepID && !$0.isAuxiliary }) else { continue }
            let id = nodeID(runs[runIndex], suffix: imagesSuffix + gallery.step)
            guard !result.nodes.contains(where: { $0.id == id }) else { continue }
            let height = imagesHeight(count: gallery.count, expanded: expanded.contains(id))
            let y = step.frame.minY
            let right = result.nodes.filter { $0.frame.minY < y + height && $0.frame.maxY > y }.map(\.frame.maxX).max() ?? step.frame.maxX
            let frame = CGRect(x: max(right, step.frame.maxX) + executionGap, y: y, width: imagesWidth, height: height)
            result.nodes.append(Node(id: id, content: .images(runIndex, gallery.step), frame: frame))
            result.size.width = max(result.size.width, frame.maxX + 24 - result.originX)
            result.size.height = max(result.size.height, frame.maxY + 24 - result.originY)
        }
        // Execution blocks hang off their request's top, stacked, right of
        // every card they would share rows with — attachments, like the file
        // list: no edge, no centreline, and nothing already placed moves.
        var nextY: [Int: CGFloat] = [:]
        for execution in executions {
            guard let runIndex = runs.firstIndex(where: { $0.id == execution.runID }),
                  let request = result.nodes.first(where: { $0.content == .request(runIndex) }) else { continue }
            let id = nodeID(runs[runIndex], suffix: executionSuffix + execution.key)
            guard !result.nodes.contains(where: { $0.id == id }) else { continue }
            let y = nextY[runIndex] ?? request.frame.minY
            let height = executionHeight(expanded: expanded.contains(id))
            let right = result.nodes.filter { $0.frame.minY < y + height && $0.frame.maxY > y }.map(\.frame.maxX).max() ?? request.frame.maxX
            let frame = CGRect(x: max(right, request.frame.maxX) + executionGap, y: y, width: executionWidth, height: height)
            result.nodes.append(Node(id: id, content: .execution(runIndex, execution.key), frame: frame))
            result.size.width = max(result.size.width, frame.maxX + 24 - result.originX)
            result.size.height = max(result.size.height, frame.maxY + 24 - result.originY)
            nextY[runIndex] = frame.maxY + executionGap
        }
        result.fittedResultID = Self.fittedResultID(runs: runs, viewport: viewport, sharedResultSize: sharedResultSize)
        return result
    }
}
