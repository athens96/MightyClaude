import CoreGraphics
import Foundation

/// How a Mighty pane draws its requests: the diagram, or the timeline (concept D).
/// Saved on the pane as `RunSession.graphViewMode`; a pane saved before the timeline
/// existed, or one holding a word this build does not know, reads as the diagram.
public enum MightyGraphViewMode: String, Codable, Sendable, CaseIterable {
    case diagram, timeline
}

extension DesignTone {
    /// The tone of a graph block or request status, through the same buckets the phone's
    /// blocks are sent in (`MobileMightySupport.status`): failed is an error, cancelled
    /// and interrupted are stops, and anything still in motion — idle, starting, queued,
    /// or a word this build has never seen — runs. The diagram's pills, its blue edges
    /// and the timeline's nodes all read this one rule.
    public init(blockStatus: String) {
        self.init(status: MobileMightySupport.status(blockStatus))
    }
}

/// The Mighty graph drawn as a vertical timeline (concept D, frame 3 B): one group per
/// request, its blocks as round nodes on a rail, the request's result under them.
///
/// It reads the very `[MightyGraphRun]` the diagram lays out, through the projection the
/// phone's timeline already reads (`MobileMightySupport.blocks`), so a block has one
/// title, one order and one status bucket on the Mac's diagram, the Mac's timeline and
/// the phone. Every count it gives is of blocks the pane holds; nothing here knows how
/// many blocks a request will end with, so nothing claims to.
public enum MightyTimeline {
    /// One block's node and the rail under it.
    public struct Node: Equatable, Sendable {
        public var tone: DesignTone
        /// The running node spreads a ring.
        public var ring: Bool
        /// The rail below the node: lit in the node's tone once the block finished well
        /// or while it runs; nil leaves it as the empty track.
        public var rail: DesignTone?
    }

    /// "블록 n개 · 끝남 m": the blocks present, never the blocks to come.
    public struct Tally: Equatable, Sendable {
        public var total: Int
        public var settled: Int
        public var running: Int
    }

    public struct Row: Equatable, Sendable, Identifiable {
        /// The block as the phone is sent it.
        public var block: MobileBlock
        /// The diagram node this row stands for (`MightyGraphLayout.Node.id`).
        public var nodeID: String
        /// Index into the run's `agents`; nil for the request's own block.
        public var agentIndex: Int?
        public var node: Node
        /// What the block is doing now, while it is in motion.
        public var latest: String?
        public var id: String { nodeID }
    }

    /// The card under a request once the diagram draws its result card.
    public struct Result: Equatable, Sendable {
        public var nodeID: String
        /// done, err or stop: the header strip's fill.
        public var tone: DesignTone
        /// The final answer the diagram's result card shows; nil when there is none.
        public var text: String?
    }

    public struct Group: Equatable, Sendable, Identifiable {
        /// Index into the runs the diagram draws, so `ordinal == runIndex + 1`.
        public var runIndex: Int
        public var runID: String
        public var ordinal: Int
        /// The request's status, bucketed as a block's is.
        public var status: String
        public var input: String
        public var rows: [Row]
        public var tally: Tally
        public var result: Result?
        public var id: String { runID }
    }

    public static func node(status: String) -> Node {
        let tone = DesignTone(status: status)
        let lit = status == "completed" || status == "running"
        return Node(tone: tone, ring: status == "running", rail: lit ? tone : nil)
    }

    /// The rail above a row takes what the row before it left (nil: the track). The
    /// first row has no rail above it at all.
    public static func railAbove(_ rows: [Row], at index: Int) -> DesignTone? {
        index > 0 && index < rows.count ? rows[index - 1].node.rail : nil
    }

    public static func tally(_ blocks: [MobileBlock]) -> Tally {
        var tally = Tally(total: blocks.count, settled: 0, running: 0)
        for block in blocks {
            if MobileMightySupport.settled(block.status) { tally.settled += 1 }
            else if block.status == "running" { tally.running += 1 }
        }
        return tally
    }

    /// Every request the diagram draws, oldest first, numbered as the diagram numbers them.
    public static func groups(_ runs: [MightyGraphRun]) -> [Group] {
        runs.enumerated().map { group($1, index: $0) }
    }

    public static func group(_ run: MightyGraphRun, index: Int) -> Group {
        let blocks = MobileMightySupport.blocks(run, ordinal: index + 1)
        var rows: [Row] = []
        rows.reserveCapacity(blocks.count)
        for (position, block) in blocks.enumerated() {
            // `blocks` is the request, then `run.agents` in order.
            let agentIndex = position == 0 ? nil : position - 1
            let suffix = agentIndex.map { "agent:" + run.agents[$0].id } ?? "request"
            let latest = block.status == "running" || block.status == "waiting" ? block.activity?.last : nil
            rows.append(Row(block: block, nodeID: MightyGraphLayout.nodeID(run, suffix: suffix), agentIndex: agentIndex,
                            node: node(status: block.status), latest: latest))
        }
        var result: Result?
        if MightyGraphLayout.finished(run) {
            let tone = DesignTone(blockStatus: run.status)
            let text = run.resultEntries.last(where: { $0.kind == "assistant" && !$0.text.isEmpty })?.text
            result = Result(nodeID: MightyGraphLayout.nodeID(run, suffix: "result"), tone: tone == .run ? .done : tone, text: text)
        }
        return Group(runIndex: index, runID: run.id, ordinal: index + 1, status: MobileMightySupport.status(run.status),
                     input: run.input, rows: rows, tally: tally(blocks), result: result)
    }
}

extension MightyGraphLayout {
    /// The diagram nodes of requests and blocks that are running, by the same rule as
    /// their pills (`DesignTone(blockStatus:)`): the edges into them are drawn blue.
    public static func liveNodeIDs(_ runs: [MightyGraphRun]) -> Set<String> {
        var ids = Set<String>()
        for run in runs {
            if DesignTone(blockStatus: run.status) == .run { ids.insert(nodeID(run, suffix: "request")) }
            for agent in run.agents where DesignTone(blockStatus: agent.status) == .run {
                ids.insert(nodeID(run, suffix: "agent:" + agent.id))
            }
        }
        return ids
    }

    /// `routes(in:)`, split by whether the edge runs into one of `targets`.
    public func routes(in visible: CGRect? = nil, into targets: Set<String>) -> (into: [[CGPoint]], other: [[CGPoint]]) {
        let frames = Dictionary(nodes.map { ($0.id, $0.frame) }, uniquingKeysWith: { first, _ in first })
        var into: [[CGPoint]] = [], other: [[CGPoint]] = []
        for edge in edges {
            guard let source = frames[edge.source], let target = frames[edge.target] else { continue }
            if let visible, !visible.intersects(source.union(target)) { continue }
            let route = Self.route(from: source, to: target, joins: edge.joins)
            if targets.contains(edge.target) { into.append(route) } else { other.append(route) }
        }
        return (into, other)
    }
}
