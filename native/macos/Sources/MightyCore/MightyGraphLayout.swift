import CoreGraphics
import Foundation

/// Each turn remains a separate tree. Coordinates are computed from complete
/// subtree bounds, so expanding one card moves every following row with it.
public struct MightyGraphLayout {
    public enum Content: Hashable {
        /// `execution` is a run index and an `OuroborosExecutionLink.key`.
        /// `images` is a run index and the step it attaches to (`MightyGraphImages`).
        /// `agent` is a run index and an index into the blocks the layout drew for that run: its `agents`, or with a
        /// fan-out (`make(fanOut:)`) its `flowAgents`, which begin with the same blocks at the same indexes.
        case request(Int), agent(Int, Int), result(Int), resultFiles(Int), execution(Int, String), images(Int, String), draft
        /// The 실행 block under a request (`make(splitsExecution:)`): what the main
        /// agent did because of the request, a run index.
        case work(Int)
        /// The top of the diagram: loads older requests from the session record.
        case history
        /// A run's pending plan approval (`PlanCardSupport`), in the flow where
        /// its result will go; `planRecord` is a run index and a `PlanRecord.id`,
        /// an answered plan attached beside its request.
        case plan(Int), planRecord(Int, String)
        /// The line on the branch from a block to the sub-agents it delegated to (`FlowDelegation`): a run index and
        /// the block's key (`FlowDelegation.mainKey`, or a sub-agent's id). Only with a fan-out.
        case delegate(Int, String)
        /// The way back to the main session, where a run's result will go, until the result is there (`FlowReturn`):
        /// a run index. Only with a fan-out.
        case returning(Int)
        /// A step of a request's checklist on its spine (`FlowSteps`): a run index and an index into its steps. Only with a fan-out.
        case step(Int, Int)
    }
    public struct Node: Identifiable {
        public let id: String
        public let content: Content
        public var frame: CGRect
        /// A sub-agent drawn as its compact card (`FlowCard`), not as its block.
        public var isCompact = false
        public var isResultFiles: Bool { if case .resultFiles = content { return true }; return false }
        /// Neither selected nor resized: a click goes to what it holds and a wheel pans past it. The history block at
        /// the top, and a compact card, which has nothing to scroll.
        public var isFixed: Bool {
            if isCompact { return true }
            switch content {
            case .history, .delegate, .returning: return true
            default: return false
            }
        }
        /// Attachments beside the flow: scrolled at once.
        public var isAuxiliary: Bool {
            switch content {
            case .resultFiles, .execution, .images, .planRecord: return true
            default: return false
            }
        }
        /// Blocks with the corner handle and the edge bands: the flow's own
        /// blocks, and the answered plans although they hang beside it.
        public var isResizable: Bool {
            if isCompact { return false }
            switch content {
            case .request, .work, .agent, .result, .draft, .plan, .planRecord, .step: return true
            case .resultFiles, .execution, .images, .history, .delegate, .returning: return false
            }
        }
        /// The least a drag may make it: an answered plan may stay as short as it folds.
        public var minimumSize: CGSize {
            if case .plan = content { return MightyGraphLayout.planMinimumSize }
            if case .planRecord = content { return MightyGraphLayout.planRecordMinimumSize }
            return CGSize(width: MightyGraphBlockSize.minimumWidth, height: MightyGraphBlockSize.minimumHeight)
        }
    }
    /// A background execution's block, attached beside the request that started it.
    public struct Execution: Hashable, Sendable {
        public var runID: String
        public var key: String
        public init(runID: String, key: String) { self.runID = runID; self.key = key }
    }
    /// The Mac's role view of a request (`make(fanOut:)`): every sub-agent of the run stands under the block that
    /// launched it, a background one too, which the other views leave to the strip and the notice runs; a running one
    /// is a compact card (`FlowCard`) unless the user opened it.
    /// Every block but the result is a card there until opened: the 실행 block, the request's steps (`FlowSteps`), each on
    /// the request's spine with what it launched beside it, and every sub-agent and background command.
    public struct FanOut: Equatable, Sendable {
        /// The nodes the user opened from their card into the full block.
        public var opened: Set<String>
        public init(opened: Set<String> = []) { self.opened = opened }
    }
    /// The delegate line's and the return node's suffixes (`Content.delegate`, `Content.returning`).
    public static let delegateSuffix = "delegate:"
    public static let returnSuffix = "return"
    /// From a block's bottom to its delegate line, and from the line to the row under it.
    static let delegateGap: CGFloat = 18
    static let delegateRowGap: CGFloat = 26
    public static let executionSuffix = "ouroboros-execution:"
    /// The 실행 block's suffix (`Content.work`).
    public static let workSuffix = "execution"
    /// The height of a request block that holds its input alone, over its 실행 block.
    public static func requestHeight(expanded: Bool) -> CGFloat { expanded ? 280 : 140 }
    /// An answered plan's block (`PlanRecord`), attached beside its request.
    public struct PlanRecordBlock: Hashable, Sendable {
        public var runID: String
        public var recordID: String
        public init(runID: String, recordID: String) { self.runID = runID; self.recordID = recordID }
    }
    public static let planSuffix = "plan"
    public static let planRecordSuffix = "plan-record:"
    /// The pending plan card without a viewport (the parity vectors): wide
    /// enough to read a plan like a document. With one it fits the pane as the
    /// newest result does (`planSize`).
    public static let planWidth: CGFloat = 640
    public static let planHeight: CGFloat = 520
    /// Only the plan is in the block (its answers are in the composer), so it
    /// may be dragged as small as any block.
    public static let planMinimumSize = CGSize(width: MightyGraphBlockSize.minimumWidth, height: MightyGraphBlockSize.minimumHeight)
    public static let planRecordWidth: CGFloat = 320
    public static func planRecordHeight(expanded: Bool) -> CGFloat { expanded ? 420 : 104 }
    /// An answered plan may be dragged as short as it folds, so its first drag
    /// starts where it stands instead of jumping to the blocks' least height.
    public static let planRecordMinimumSize = CGSize(width: MightyGraphBlockSize.minimumWidth, height: planRecordHeight(expanded: false))
    public static let executionWidth: CGFloat = 380
    static let executionGap: CGFloat = 8
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
        /// From a spine block's trailing edge to a block it launched, beside it (`FlowSteps`).
        public var side: Bool = false
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
    /// The newest result card's node id when the viewport limit, not its
    /// saved size or window fit, decides its size in some direction: it then
    /// follows the pane as a fitted card does. nil without a limit.
    public var viewportBoundResultID: String? = nil
    /// The viewport limit on the newest result card (`resultViewportLimit`)
    /// and the window fit in force (`resultFitSize`), for a drag of it; nil
    /// without a viewport (the limit also without a zoom) or a result.
    public var resultLimit: CGSize? = nil
    public var resultWindowFit: CGSize? = nil
    /// The same two for the pending plan block (`planSize`), for a drag of it;
    /// nil without a plan block or a viewport (the limit also without a zoom).
    public var planLimit: CGSize? = nil
    public var planWindowFit: CGSize? = nil
    static let siblingGap: CGFloat = 20
    static let rowGap: CGFloat = 32
    public static let historyNodeID = "history-top"
    public static let historyWidth: CGFloat = 380
    public static let historyHeight: CGFloat = 44

    /// Used by the initial SwiftUI render as well as native camera admission.
    /// Computing this before mounting avoids painting the origin then jumping.
    public static func cameraOffset(for frame: CGRect, viewport: CGSize, zoom: CGFloat, alignTop: Bool) -> CGPoint {
        CGPoint(x: (viewport.width - frame.width * zoom) / 2 - frame.minX * zoom,
                y: (alignTop ? 16 : max(16, (viewport.height - frame.height * zoom) / 2)) - frame.minY * zoom)
    }
    /// A newly finished result card: its bottom 16pt above the viewport's
    /// bottom edge, right above the composer, or — taller than the viewport —
    /// its top where `alignTop` puts it, so its beginning is what shows.
    public static func revealOffset(for frame: CGRect, viewport: CGSize, zoom: CGFloat) -> CGPoint {
        let top = cameraOffset(for: frame, viewport: viewport, zoom: zoom, alignTop: true)
        return CGPoint(x: top.x, y: max(top.y, viewport.height - 16 - frame.maxY * zoom))
    }
    /// `alignBottom` (a revealed result) wins over `alignTop`.
    public static func cameraOffset(for frame: CGRect, viewport: CGSize, zoom: CGFloat, alignTop: Bool, alignBottom: Bool) -> CGPoint {
        alignBottom ? revealOffset(for: frame, viewport: viewport, zoom: zoom)
            : cameraOffset(for: frame, viewport: viewport, zoom: zoom, alignTop: alignTop)
    }

    /// The newest result card is never shorter than a dragged block may be.
    public static let minimumResultHeight = CGFloat(MightyGraphBlockSize.minimumHeight)
    /// The newest result card's size. `cap` is the size the user last dragged
    /// it to or, with none saved, the window fit: a remembered maximum, not a
    /// fixed size. The card is as tall as its content (header included) up to
    /// that cap, where its answer scrolls inside, and never shorter than
    /// `minimumResultHeight`. Content not measured yet takes the cap. The
    /// width stays the cap's: the answer wraps to whatever width it is given.
    public static func resultSize(cap: CGSize, contentHeight: CGFloat?) -> CGSize {
        guard let contentHeight, contentHeight.isFinite, contentHeight >= 0 else { return cap }
        return CGSize(width: cap.width, height: min(cap.height, max(minimumResultHeight, ceil(contentHeight))))
    }
    /// The window fit: the newest result card's cap when no size is saved.
    public static func resultFitSize(viewport: CGSize, filesPanelOpen: Bool) -> CGSize {
        CGSize(width: max(500, viewport.width - 48 - (filesPanelOpen ? 336 : 0)), height: max(200, viewport.height - 48))
    }
    /// The most of the pane the newest result card may take, in diagram
    /// coordinates at `zoom`: the viewport less the window fit's 24pt margins
    /// (screen points, so they stay 24pt at any zoom) and, with the result
    /// files panel open, that panel and its gap beside the card. Drawn at
    /// `zoom` the card — and its panel — stay inside the visible canvas, so
    /// the reveal can always put its bottom right above the composer. Never
    /// below a dragged block's minimum, so a tiny pane keeps a usable card.
    public static func resultViewportLimit(viewport: CGSize, zoom: CGFloat, filesPanelOpen: Bool) -> CGSize {
        let scale = zoom.isFinite && zoom > 0 ? zoom : 1
        return CGSize(width: max(CGFloat(MightyGraphBlockSize.minimumWidth), (viewport.width - 48) / scale - (filesPanelOpen ? 336 : 0)),
                      height: max(minimumResultHeight, (viewport.height - 48) / scale))
    }
    /// The newest result card's cap in force: `maximum` (the saved size, else
    /// the window fit) kept within `limit` side by side; nil keeps it whole.
    /// Only what is drawn shrinks — the saved size is left as it is, so the
    /// card grows back to it when the pane does. A drag in progress is kept
    /// within the same limit.
    public static func resultCap(_ maximum: CGSize, within limit: CGSize?) -> CGSize {
        guard let limit else { return maximum }
        return CGSize(width: min(maximum.width, limit.width), height: min(maximum.height, limit.height))
    }
    /// The pending plan block's size, by the newest result card's rules: the
    /// size the user last dragged it to (`saved`, one per pane) or, with none,
    /// the window fit is the most it takes, kept within the pane at `zoom` so
    /// it shrinks and grows back with the pane. The plan scrolls inside, so it
    /// never shrinks to its content. Without a viewport it is the saved size or
    /// `planWidth` × `planHeight`. Also returns the limit and the window fit in
    /// force, for a drag of it (`resultDrag`).
    public static func planSize(saved: MightyGraphBlockSize?, viewport: CGSize?, zoom: CGFloat?) -> (size: CGSize, limit: CGSize?, windowFit: CGSize?) {
        let savedSize = saved?.normalized.map { CGSize(width: $0.width, height: $0.height) }
        guard let viewport else { return (savedSize ?? CGSize(width: planWidth, height: planHeight), nil, nil) }
        // No files panel stands beside a plan.
        let fit = resultFitSize(viewport: viewport, filesPanelOpen: false)
        let limit = zoom.map { resultViewportLimit(viewport: viewport, zoom: $0, filesPanelOpen: false) }
        return (resultCap(savedSize ?? fit, within: limit), limit, fit)
    }
    /// Whether the result files panel is open beside the newest result card.
    public static func filesPanelOpen(runs: [MightyGraphRun], resultFilesRunID: String?) -> Bool {
        guard let index = runs.indices.last(where: { hasResult(runs[$0]) }), let resultFilesRunID,
              runs[index].id == resultFilesRunID, runs[index].status == "completed" else { return false }
        return true
    }
    /// Where a block drag is: still moving, released, or abandoned (Escape,
    /// the window losing key) with the block back at its starting size.
    public enum ResizePhase: Equatable, Sendable { case live, finished, cancelled }
    /// A drag of the newest result card, both what it shows and what it saves.
    /// `dragged` is the size under the cursor; `limit` the pane's
    /// (`resultViewportLimit`, nil for none); `windowFit` the window fit in
    /// force (`resultFitSize`, nil without a viewport).
    ///
    /// `live` (only while it moves): the dragged size kept within the limit,
    /// so the card follows the cursor exactly up to the pane's edge and stops
    /// there. `save` (only on release; nil when cancelled) is the new
    /// remembered maximum, side by side:
    /// - a side the drag did not move keeps the maximum in force — the saved
    ///   size, else the window fit, never the smaller size shown;
    /// - a moved side released at (or pushed past) the limit keeps the larger
    ///   of that maximum and the limit: the card was at the pane's edge, so
    ///   the drag cannot tell a smaller size, and a narrow pane never
    ///   overwrites a larger saved size with the size it shrank the card to;
    /// - a moved side released inside the limit saves what it was released at.
    public static func resultDrag(dragged: CGSize, edges: ResizeEdges, phase: ResizePhase, saved: MightyGraphBlockSize?,
                                  limit: CGSize?, windowFit: CGSize?) -> (live: MightyGraphBlockSize?, save: MightyGraphBlockSize?) {
        switch phase {
        case .cancelled: return (nil, nil)
        case .live:
            let shown = resultCap(dragged, within: limit)
            return (MightyGraphBlockSize(width: shown.width, height: shown.height).normalized, nil)
        case .finished:
            func side(moved: Bool, dragged: CGFloat, saved: CGFloat?, fit: CGFloat?, limit: CGFloat?) -> CGFloat {
                let maximum = saved ?? fit
                guard moved else { return maximum ?? dragged }
                guard let limit, dragged >= limit - 0.5 else { return dragged }
                return max(maximum ?? limit, limit)
            }
            let width = side(moved: edges.horizontal, dragged: dragged.width, saved: saved.map { CGFloat($0.width) },
                             fit: windowFit?.width, limit: limit?.width)
            let height = side(moved: edges.vertical, dragged: dragged.height, saved: saved.map { CGFloat($0.height) },
                              fit: windowFit?.height, limit: limit?.height)
            return (nil, MightyGraphBlockSize(width: width, height: height).normalized)
        }
    }

    public func route(_ edge: Edge) -> [CGPoint] {
        guard let source = nodes.first(where: { $0.id == edge.source })?.frame,
              let target = nodes.first(where: { $0.id == edge.target })?.frame else { return [] }
        return Self.route(from: source, to: target, joins: edge.joins, side: edge.side)
    }
    /// Every edge's route with the nodes looked up once. With `visible`, an
    /// edge whose whole span lies outside it is left out, as offscreen cards are.
    public func routes(in visible: CGRect? = nil) -> [[CGPoint]] {
        let frames = Dictionary(nodes.map { ($0.id, $0.frame) }, uniquingKeysWith: { first, _ in first })
        return edges.compactMap { edge in
            guard let source = frames[edge.source], let target = frames[edge.target] else { return nil }
            if let visible, !visible.intersects(source.union(target)) { return nil }
            return Self.route(from: source, to: target, joins: edge.joins, side: edge.side)
        }
    }
    static func route(from source: CGRect, to target: CGRect, joins: Bool, side: Bool = false) -> [CGPoint] {
        if side {
            // Out of the trailing edge at the card's middle (a block's header), along a lane in the gap, into the leading edge.
            let start = CGPoint(x: source.maxX, y: source.minY + min(source.height / 2, FlowCard.size.height / 2))
            let end = CGPoint(x: target.minX, y: target.minY + min(target.height / 2, FlowCard.size.height / 2))
            let lane = source.maxX + min(FlowSteps.sideGap / 2, max(0, (end.x - start.x) / 2))
            return [start, CGPoint(x: lane, y: start.y), CGPoint(x: lane, y: end.y), end]
        }
        let start = CGPoint(x: source.midX, y: source.maxY)
        let end = CGPoint(x: target.midX, y: target.minY)
        let clearance = min(26, max(0, (end.y - start.y) / 2))
        let middle = joins ? end.y - clearance : start.y + clearance
        return [start, CGPoint(x: start.x, y: middle), CGPoint(x: end.x, y: middle), end]
    }

    public static func terminal(_ state: String) -> Bool {
        ["completed", "error", "failed", "stopped", "cancelled", "interrupted"].contains(state)
    }
    /// Ended with every block a view draws for it (`BackgroundNoticeRuns.shownAgents`): background work the
    /// request started runs on in the strip and is not waited for.
    public static func finished(_ run: MightyGraphRun) -> Bool {
        terminal(run.status) && BackgroundNoticeRuns.shownAgents(run).allSatisfy { terminal($0.status) }
    }
    /// Has a result card under it: every finished run but a notice run without a reply of its own
    /// (`MightyGraphRun.quietNotice`), whose card would hold nothing; and a notice run a later turn answers in again,
    /// which keeps showing its earlier reply (`MightyGraphRun.reopenedWithReply`).
    public static func hasResult(_ run: MightyGraphRun) -> Bool { (finished(run) && !run.quietNotice) || run.reopenedWithReply }
    public static func nodeID(_ run: MightyGraphRun, suffix: String) -> String {
        MightyGraphBlockSize.nodeID(runID: run.id, suffix: suffix)
    }

    /// The block the camera stands on for the newest request: its 실행 block while
    /// it runs, where its work shows, else the request; nil without a request. A
    /// notice run after it is not a request (`MightyGraphSupport.newestRequestRun`).
    public func newestRequestTargetID(_ runs: [MightyGraphRun]) -> String? {
        guard let last = MightyGraphSupport.newestRequestRun(runs) else { return nil }
        let work = Self.nodeID(last, suffix: Self.workSuffix)
        if !Self.finished(last), nodes.contains(where: { $0.id == work }) { return work }
        return Self.nodeID(last, suffix: "request")
    }

    /// The newest result card: the newest run that has one (`hasResult`).
    public static func latestResultID(runs: [MightyGraphRun]) -> String? {
        runs.indices.last(where: { hasResult(runs[$0]) }).map { nodeID(runs[$0], suffix: "result") }
    }

    public static func fittedResultID(runs: [MightyGraphRun], viewport: CGSize?, sharedResultSize: MightyGraphBlockSize?) -> String? {
        (viewport != nil && sharedResultSize == nil) ? latestResultID(runs: runs) : nil
    }

    /// `retainedStart` is the index of the run that keeps the first tree's
    /// place (the first run the pane retained, or the one loaded history first
    /// attached above); runs before it, loaded from the session record, stack
    /// upward from there. `history` adds the block at the top that loads more.
    /// `resultContentHeight` is the newest result card's measured content
    /// height (`resultSize(cap:contentHeight:)`); nil keeps it at its cap.
    /// `zoom`, with a viewport, also keeps the newest result card within the
    /// viewport at that zoom (`resultViewportLimit`); nil leaves its cap whole.
    /// `planSize` is the pane's saved plan block size (`RunSession.graphPlanSize`,
    /// or a drag in progress); see `planSize(saved:viewport:zoom:)`.
    /// `splitsExecution` is the Mac diagram's own: a request block holds its input
    /// alone and a 실행 block (`Content.work`) under it holds the main agent's work,
    /// with the sub-agents, the step's pictures, the background executions and the
    /// answered plans hanging off it. Off, the request block holds both, as the
    /// shared graph vectors (`native/contracts/graph-vectors.json`) lay it out.
    /// `fanOut` is the Mac's role view of a Claude pane (`FanOut`): the run's `flowAgents` are laid out, a background
    /// sub-agent under the block that launched it, and a sub-agent that is a card takes `FlowCard.size` with nothing
    /// drawn under it. Nil lays out `agents` as full blocks, as every other caller and the shared vectors do.
    public static func make(runs: [MightyGraphRun], draft: String, running: Bool, expanded: Set<String>, blockSizes: [String: MightyGraphBlockSize] = [:], resultFilesRunID: String? = nil, viewport: CGSize? = nil, zoom: CGFloat? = nil, sharedResultSize: MightyGraphBlockSize? = nil, resultContentHeight: CGFloat? = nil, executions: [Execution] = [], galleries: [ImageGallery] = [], retainedStart: Int = 0, history: Bool = false,
                            planRunID: String? = nil, planRecords: [PlanRecordBlock] = [], planSize savedPlanSize: MightyGraphBlockSize? = nil,
                            splitsExecution: Bool = false, fanOut: FanOut? = nil) -> Self {
        let latestResultID = Self.latestResultID(runs: runs)

        // The files panel reduces available width only when it is open for the latest result.
        let filesPanelOpenForLatest = filesPanelOpen(runs: runs, resultFilesRunID: resultFilesRunID)

        // Auto-fit size for the latest result card (no .normalized clamp applied).
        let autoFitResultSize: CGSize? = {
            guard let vp = viewport, latestResultID != nil else { return nil }
            return resultFitSize(viewport: vp, filesPanelOpen: filesPanelOpenForLatest)
        }()
        let resultLimit: CGSize? = {
            guard let vp = viewport, let zoom, latestResultID != nil else { return nil }
            return resultViewportLimit(viewport: vp, zoom: zoom, filesPanelOpen: filesPanelOpenForLatest)
        }()

        func size(_ id: String, width: CGFloat, height: CGFloat) -> CGSize {
            // Latest result card: the shared or auto-fit size, when a viewport
            // is active, is the most it takes, kept within the viewport; it
            // shrinks to its content.
            if let latestID = latestResultID, id == latestID, viewport != nil {
                if let shared = sharedResultSize {
                    return resultSize(cap: resultCap(CGSize(width: shared.width, height: shared.height), within: resultLimit), contentHeight: resultContentHeight)
                }
                if let autoFit = autoFitResultSize { return resultSize(cap: resultCap(autoFit, within: resultLimit), contentHeight: resultContentHeight) }
            }
            guard let custom = blockSizes[id]?.normalized(nodeID: id) else { return CGSize(width: width, height: height) }
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
        var planFit: (size: CGSize, limit: CGSize?, windowFit: CGSize?)?
        for (runIndex, run) in runs.enumerated() {
            let mainID = nodeID(run, suffix: "request")
            let mainSize = size(mainID, width: MightyGraphCamera.requestWidth,
                                height: splitsExecution ? requestHeight(expanded: expanded.contains(mainID)) : expanded.contains(mainID) ? 540 : 280)
            let mainHeight = mainSize.height
            let workID = nodeID(run, suffix: workSuffix)
            let workSize = splitsExecution ? size(workID, width: MightyGraphCamera.requestWidth, height: expanded.contains(workID) ? 540 : 280) : nil
            let resultID = nodeID(run, suffix: "result")
            let resultSize = size(resultID, width: MightyGraphCamera.requestWidth, height: expanded.contains(resultID) ? 440 : 200)
            var visited = Set<Int>()
            // The blocks launched from the main session in one of the request's steps: their cards name the step.
            let inStep: Set<Int> = fanOut == nil ? [] : Set(run.flowAgents.indices.filter { FlowSteps.stepLabel(run.flowAgents[$0], in: run) != nil })
            // With a fan-out the run's background sub-agents are laid out too, each where it was launched.
            let agents = fanOut == nil ? run.agents : run.flowAgents
            func ordered(_ indexes: [Int]) -> [Int] {
                fanOut == nil ? indexes : indexes.sorted { run.flowOrder($0) < run.flowOrder($1) }
            }
            let indexes = Dictionary(agents.enumerated().map { ($0.element.id, $0.offset) }, uniquingKeysWith: { first, _ in first })
            /// A card counts the blocks under it instead of drawing them: they are laid out nowhere.
            func fold(_ index: Int, depth: Int) {
                guard depth < 64, visited.insert(index).inserted else { return }
                for child in agents.indices where child != index && agents[child].parentID == agents[index].id { fold(child, depth: depth + 1) }
            }
            func branch(_ index: Int, depth: Int = 0) -> Tree? {
                guard depth < 64, visited.insert(index).inserted else { return nil }
                let agent = agents[index]
                let id = nodeID(run, suffix: "agent:\(agent.id)")
                let compact = fanOut.map { FlowCard.isCompact(agent, opened: $0.opened.contains(id)) } ?? false
                // A background run with Claude's reply to its notice holds its command, output
                // lines and that reply above its summary, so it opens taller. A card beside a step says which (`FlowSteps.stepLabel`).
                let cardSize = compact ? (inStep.contains(index) ? FlowCard.stepSize : FlowCard.size)
                    : size(id, width: 360, height: expanded.contains(id) ? 480 : agent.isTask && agent.noticeReply != nil ? 320 : 240)
                let height = cardSize.height
                let below = ordered(agents.indices.filter { $0 != index && agents[$0].parentID == agent.id })
                if compact { for child in below { fold(child, depth: depth + 1) } }
                let children = compact ? [] : below.compactMap { branch($0, depth: depth + 1) }
                let childrenWidth = children.reduce(CGFloat(0)) { $0 + $1.width } + CGFloat(max(0, children.count - 1)) * siblingGap
                // A block that delegated says so on the branch to its row (`FlowDelegation`).
                let delegates = fanOut != nil && !children.isEmpty && !FlowDelegation.children(of: agent.id, in: run).isEmpty
                let width = max(cardSize.width, childrenWidth, delegates ? FlowDelegation.size.width : 0)
                var result = Tree(nodes: [Node(id: id, content: .agent(runIndex, index), frame: CGRect(x: (width - cardSize.width) / 2, y: 0, width: cardSize.width, height: height), isCompact: compact)], edges: [], width: width, height: height, top: id, leaves: [id])
                if !children.isEmpty {
                    result.leaves = []
                    var source = id
                    var top = height + rowGap
                    if delegates {
                        let line = nodeID(run, suffix: delegateSuffix + agent.id)
                        let frame = CGRect(x: (width - FlowDelegation.size.width) / 2, y: height + delegateGap, width: FlowDelegation.size.width, height: FlowDelegation.size.height)
                        result.nodes.append(Node(id: line, content: .delegate(runIndex, agent.id), frame: frame))
                        result.edges.append(Edge(source: id, target: line))
                        source = line
                        top = frame.maxY + delegateRowGap
                    }
                    var x = (width - childrenWidth) / 2
                    for var child in children {
                        child.offset(x: x, y: top)
                        result.nodes += child.nodes
                        result.edges += [Edge(source: source, target: child.top)] + child.edges
                        result.leaves += child.leaves
                        result.height = max(result.height, top + child.height)
                        x += child.width + siblingGap
                    }
                }
                return result
            }
            func isTop(_ index: Int) -> Bool {
                let parent = agents[index].parentID
                return parent == nil || indexes[parent!] == nil || parent == agents[index].id
            }
            // A plan waiting for its answer stands where the result will.
            let planWaits = run.id == planRunID && !finished(run)
            // The role view (`FanOut`): the request, what happened between it and its answer, and the answer. While there is
            // no answer yet the main session's card stands in its place (the 실행 block, a card until opened).
            let endsInWork = fanOut != nil && splitsExecution && !hasResult(run) && !planWaits
            let workCompact = fanOut.map { !$0.opened.contains(workID) } ?? false
            let workShown = workCompact ? FlowSteps.cardSize : workSize
            // The request's steps that began, by their index among all its steps (`Content.step`); one not begun is only
            // in the plan column (`Content.outline`).
            let started = fanOut == nil ? [] : FlowSteps.steps(run).enumerated().filter { !$0.element.isPending }.map { (index: $0.offset, step: $0.element) }
            let width: CGFloat
            var tree: Tree
            if !started.isEmpty {
                // The request's spine: the request, then each step that began, every block launched from the main session
                // beside the one whose time it was launched in (`FlowSteps.slot`; before the first step, beside the request).
                let spineSteps = started.map(\.step)
                var slots = [[Tree]](repeating: [], count: started.count + 1)
                for index in ordered(Array(agents.indices)) where isTop(index) {
                    if let branch = branch(index) { slots[FlowSteps.slot(of: agents[index], steps: spineSteps)].append(branch) }
                }
                for index in agents.indices where !visited.contains(index) {
                    if let branch = branch(index) { slots[0].append(branch) }
                }
                var spine: [(id: String, content: Content, size: CGSize, compact: Bool)] = [(mainID, .request(runIndex), mainSize, false)]
                for (index, step) in started {
                    let id = nodeID(run, suffix: FlowSteps.nodeSuffix(step))
                    let compact = fanOut.map { !$0.opened.contains(id) } ?? true
                    spine.append((id, .step(runIndex, index), compact ? FlowSteps.cardSize : size(id, width: 360, height: expanded.contains(id) ? 480 : 240), compact))
                }
                // The spine stands on the run's centreline: the column beside it is matched by room on the other side.
                var half = max(hasResult(run) ? resultSize.width : 0, endsInWork ? workShown?.width ?? 0 : 0) / 2
                for (slot, block) in spine.enumerated() {
                    let column = slots[slot].map(\.width).max() ?? 0
                    half = max(half, block.size.width / 2 + (column > 0 ? FlowSteps.sideGap + column : 0))
                }
                width = 2 * half
                tree = Tree(nodes: [], edges: [], width: width, height: 0, top: mainID, leaves: [])
                var y: CGFloat = 0, above: String?
                for (slot, block) in spine.enumerated() {
                    let frame = CGRect(x: half - block.size.width / 2, y: above == nil ? 0 : y + rowGap, width: block.size.width, height: block.size.height)
                    tree.nodes.append(Node(id: block.id, content: block.content, frame: frame, isCompact: block.compact))
                    if let above { tree.edges.append(Edge(source: above, target: block.id)) }
                    var bottom = frame.maxY, top = frame.minY
                    for var child in slots[slot] {
                        child.offset(x: frame.maxX + FlowSteps.sideGap, y: top)
                        tree.nodes += child.nodes
                        tree.edges += [Edge(source: block.id, target: child.top, side: true)] + child.edges
                        bottom = max(bottom, top + child.height)
                        top += child.height + siblingGap
                    }
                    y = bottom
                    above = block.id
                }
                tree.height = y
                tree.leaves = above.map { [$0] } ?? [mainID]
            } else {
                var branches: [Tree] = []
                for index in ordered(Array(agents.indices)) where isTop(index) {
                    if let tree = branch(index) { branches.append(tree) }
                }
                // Malformed/cyclic legacy data still remains visible, once per agent.
                for index in agents.indices where !visited.contains(index) {
                    if let tree = branch(index) { branches.append(tree) }
                }
                let branchWidth = branches.reduce(CGFloat(0)) { $0 + $1.width } + CGFloat(max(0, branches.count - 1)) * siblingGap
                // The role view draws the 실행 block only where the answer will be (below); every other view right under the request.
                let workUnder = fanOut == nil ? workShown : nil
                width = max(mainSize.width, workUnder?.width ?? 0, branchWidth, hasResult(run) ? resultSize.width : 0, endsInWork ? workShown?.width ?? 0 : 0)
                tree = Tree(nodes: [Node(id: mainID, content: .request(runIndex), frame: CGRect(x: (width - mainSize.width) / 2, y: 0, width: mainSize.width, height: mainHeight))], edges: [], width: width, height: mainHeight, top: mainID, leaves: [mainID])
                // The block the sub-agents branch from: the 실행 block right under the request, or the request itself.
                var parentID = mainID
                if let workUnder {
                    tree.nodes.append(Node(id: workID, content: .work(runIndex), frame: CGRect(x: (width - workUnder.width) / 2, y: mainHeight + rowGap, width: workUnder.width, height: workUnder.height)))
                    tree.edges.append(Edge(source: mainID, target: workID))
                    tree.leaves = [workID]
                    tree.height += rowGap + workUnder.height
                    parentID = workID
                }
                if !branches.isEmpty {
                    tree.leaves = []
                    var top = tree.height + rowGap
                    // The main session's delegate line stands between it and the row (`FlowDelegation`).
                    if fanOut != nil, !FlowDelegation.children(of: FlowDelegation.mainKey, in: run).isEmpty {
                        let line = nodeID(run, suffix: delegateSuffix + FlowDelegation.mainKey)
                        let frame = CGRect(x: (width - FlowDelegation.size.width) / 2, y: tree.height + delegateGap, width: FlowDelegation.size.width, height: FlowDelegation.size.height)
                        tree.nodes.append(Node(id: line, content: .delegate(runIndex, FlowDelegation.mainKey), frame: frame))
                        tree.edges.append(Edge(source: parentID, target: line))
                        parentID = line
                        top = frame.maxY + delegateRowGap
                    }
                    var x = (width - branchWidth) / 2
                    for var child in branches {
                        child.offset(x: x, y: top)
                        tree.nodes += child.nodes
                        tree.edges += [Edge(source: parentID, target: child.top)] + child.edges
                        tree.leaves += child.leaves
                        tree.height = max(tree.height, top + child.height)
                        x += child.width + siblingGap
                    }
                }
            }
            // A plan waiting for an answer goes where the result will: the
            // run is still going, so nothing is below it yet.
            if planWaits {
                let id = nodeID(run, suffix: planSuffix)
                let fit = Self.planSize(saved: savedPlanSize, viewport: viewport, zoom: zoom)
                planFit = fit
                let planSize = fit.size
                if planSize.width > tree.width {
                    let shift = (planSize.width - tree.width) / 2
                    tree.offset(x: shift, y: 0)
                    tree.width = planSize.width
                }
                tree.nodes.append(Node(id: id, content: .plan(runIndex), frame: CGRect(x: (tree.width - planSize.width) / 2, y: tree.height + rowGap, width: planSize.width, height: planSize.height)))
                tree.edges += tree.leaves.map { Edge(source: $0, target: id, joins: true) }
                tree.leaves = [id]
                tree.height += rowGap + planSize.height
            }
            // The role view: the main session at work, where its answer will come, until it is there.
            if endsInWork, let workShown {
                tree.nodes.append(Node(id: workID, content: .work(runIndex), frame: CGRect(x: (width - workShown.width) / 2, y: tree.height + rowGap, width: workShown.width, height: workShown.height), isCompact: workCompact))
                tree.edges += tree.leaves.map { Edge(source: $0, target: workID, joins: true) }
                tree.leaves = [workID]
                tree.height += rowGap + workShown.height
            }
            if hasResult(run) {
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
           runs[runIndex].status == "completed", hasResult(runs[runIndex]),
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
            // The request step's pictures come from the main agent's records: with a 실행 block they hang off it.
            let stepID = nodeID(runs[runIndex], suffix: splitsExecution && gallery.step == MightyGraphImages.requestStep ? workSuffix : gallery.step)
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
        // Execution blocks hang off their request's top (its 실행 block's, when
        // there is one: the main agent started them), stacked, right of every
        // card they would share rows with — attachments, like the file list: no
        // edge, no centreline, and nothing already placed moves.
        func anchor(_ runIndex: Int) -> Node? {
            // The role view draws the 실행 block only while the answer is not there: then the request is the anchor.
            (splitsExecution ? result.nodes.first(where: { $0.content == .work(runIndex) }) : nil)
                ?? result.nodes.first(where: { $0.content == .request(runIndex) })
        }
        var nextY: [Int: CGFloat] = [:]
        for execution in executions {
            guard let runIndex = runs.firstIndex(where: { $0.id == execution.runID }),
                  let owner = anchor(runIndex) else { continue }
            let id = nodeID(runs[runIndex], suffix: executionSuffix + execution.key)
            guard !result.nodes.contains(where: { $0.id == id }) else { continue }
            let y = nextY[runIndex] ?? owner.frame.minY
            let height = executionHeight(expanded: expanded.contains(id))
            let right = result.nodes.filter { $0.frame.minY < y + height && $0.frame.maxY > y }.map(\.frame.maxX).max() ?? owner.frame.maxX
            let frame = CGRect(x: max(right, owner.frame.maxX) + executionGap, y: y, width: executionWidth, height: height)
            result.nodes.append(Node(id: id, content: .execution(runIndex, execution.key), frame: frame))
            result.size.width = max(result.size.width, frame.maxX + 24 - result.originX)
            result.size.height = max(result.size.height, frame.maxY + 24 - result.originY)
            nextY[runIndex] = frame.maxY + executionGap
        }
        // Answered plans hang off the same block as well, below its execution
        // blocks: attachments, nothing already placed moves.
        for block in planRecords {
            guard let runIndex = runs.firstIndex(where: { $0.id == block.runID }),
                  let owner = anchor(runIndex) else { continue }
            let id = nodeID(runs[runIndex], suffix: planRecordSuffix + block.recordID)
            guard !result.nodes.contains(where: { $0.id == id }) else { continue }
            let y = nextY[runIndex] ?? owner.frame.minY
            // A dragged size wins over folded or opened, as on the other blocks.
            let recordSize = size(id, width: planRecordWidth, height: planRecordHeight(expanded: expanded.contains(id)))
            let height = recordSize.height
            let right = result.nodes.filter { $0.frame.minY < y + height && $0.frame.maxY > y }.map(\.frame.maxX).max() ?? owner.frame.maxX
            let frame = CGRect(x: max(right, owner.frame.maxX) + executionGap, y: y, width: recordSize.width, height: height)
            result.nodes.append(Node(id: id, content: .planRecord(runIndex, block.recordID), frame: frame))
            result.size.width = max(result.size.width, frame.maxX + 24 - result.originX)
            result.size.height = max(result.size.height, frame.maxY + 24 - result.originY)
            nextY[runIndex] = frame.maxY + executionGap
        }
        result.fittedResultID = Self.fittedResultID(runs: runs, viewport: viewport, sharedResultSize: sharedResultSize)
        result.resultLimit = resultLimit
        result.resultWindowFit = autoFitResultSize
        result.planLimit = planFit?.limit
        result.planWindowFit = planFit?.windowFit
        if viewport != nil, let maximum = sharedResultSize.map({ CGSize(width: $0.width, height: $0.height) }) ?? autoFitResultSize,
           resultCap(maximum, within: resultLimit) != maximum {
            result.viewportBoundResultID = latestResultID
        }
        return result
    }
}
