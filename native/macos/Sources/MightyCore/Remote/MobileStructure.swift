import CryptoKit
import Foundation

/// `MobileMighty.structure`: the Mac's own structure view (`MightyStructure.make`) of a
/// pane, cut down for the phone. The phone draws it and never works a phase or a step
/// out for itself, so the two screens cannot disagree about where a request belongs.
///
/// Versioned (`v`) and bounded hard: every text has a cap well under the Mac card's, only
/// the newest `maximumItems` requests travel, a request carries at most `maximumNodes`
/// sub-agent rows, and the whole value stays under `budgetBytes` of JSON, older requests
/// dropped first.
public struct MobileStructure: Codable, Sendable, Equatable {
    /// The shape this build writes. A phone that reads another `v` shows nothing of it.
    public static let version = 1

    public var v: Int
    /// "phases" (a style with phases: `columns`) or "free" (no style: `rows`).
    public var shape: String
    public var columns: [Column]?
    public var rows: [Row]?
    /// Older requests left out of the payload; absent when none were.
    public var omitted: Int?

    public init(v: Int = MobileStructure.version, shape: String, columns: [Column]? = nil, rows: [Row]? = nil, omitted: Int? = nil) {
        self.v = v; self.shape = shape; self.columns = columns; self.rows = rows; self.omitted = omitted
    }

    /// One phase of a styled pane and its requests, oldest first.
    public struct Column: Codable, Sendable, Equatable {
        public var id: String
        public var title: String
        /// `MightyStructure.Step`: done, current or pending.
        public var step: String
        public var items: [Item]
    }

    /// One request of a free pane as 요청 → 실행 → 결과, each a `MightyStructure.Step`.
    public struct Row: Codable, Sendable, Equatable {
        public var item: Item
        public var request: String
        public var execution: String
        public var result: String
    }

    public struct Item: Codable, Sendable, Equatable {
        public var runId: String
        /// The request's number in the pane, as its heading says it.
        public var ordinal: Int
        /// The style's own name for the request, as `MobileMightyRun.title`.
        public var title: String?
        /// A block status (`MobileWire.blockStatuses`).
        public var status: String
        public var finished: Bool
        public var request: String?
        /// The main agent's latest reply, left out once that reply is the answer.
        public var message: String?
        /// The main agent's last tool calls, oldest first.
        public var activity: [Activity]
        /// The final answer as Markdown, once the request finished with one.
        public var answer: String?
        public var tally: Tally
        public var clusters: [Cluster]
        /// Sub-agent rows left out past `maximumNodes`; absent when none were.
        public var omittedNodes: Int?
    }

    public struct Activity: Codable, Sendable, Equatable {
        /// `AgentActivity.kind`.
        public var kind: String
        public var state: String
        public var text: String
    }

    /// What the request holds now, counted over all of it even when nodes were left out.
    public struct Tally: Codable, Sendable, Equatable {
        public var agents: Int
        public var tools: Int
    }

    /// One root sub-agent's tree, the root first and its descendants depth-first.
    public struct Cluster: Codable, Sendable, Equatable {
        public var nodes: [Node]
    }

    public struct Node: Codable, Sendable, Equatable {
        /// The block's id, unique within its request.
        public var id: String
        /// 0 for the cluster's root, never past `maximumDepth`.
        public var depth: Int
        public var title: String
        public var status: String
        /// "agent" or "task".
        public var kind: String
        /// The model's short label, when the block reported one.
        public var model: String?
        public var prompt: String?
        public var output: String?
        /// Its last tool call.
        public var activity: Activity?
        /// `true` for a background sub-agent and every task; absent otherwise.
        public var background: Bool?
        /// A task's last output lines (at most 6), read on the host; never its output file.
        public var outputTail: [String]?
        /// Claude's reply to the task's notice, bounded like `output`.
        public var noticeReply: String?
    }

    // MARK: Bounds

    public static let requestBytes = 1_024
    public static let messageBytes = 1_024
    public static let answerBytes = 4_096
    public static let promptBytes = 300
    public static let outputBytes = 1_024
    public static let activityBytes = 200
    public static let titleBytes = 160
    public static let modelBytes = 80
    public static let maximumItems = 20
    public static let maximumNodes = 60
    /// Deeper nodes are drawn at this depth, as the Mac stops indenting there.
    public static let maximumDepth = MightyStructure.maximumIndent
    public static let maximumActivity = MightyStructure.requestActivity
    /// The JSON the whole value may take. Older requests go first; a newest request
    /// over it alone sheds its last nodes.
    public static let budgetBytes = 192 * 1_024

    /// What a request adds to the JSON beside its own object: the comma before it, and a
    /// free row's wrapper with its three steps. Only an estimate; `make` checks the real size.
    static let itemOverhead = 96

    // MARK: Projection

    /// One request in its wire form with its JSON size, kept between builds so a rebuild
    /// encodes only the requests that moved.
    public struct Wired: Sendable {
        var source: MightyStructure.Item
        var title: String?
        /// The output lines of the request's tasks it was built with, by block id.
        var tails: [String: [String]] = [:]
        var item: Item
        var size: Int
    }

    /// The pane's structure for the phone. Nil when there is nothing to draw: no request
    /// yet and no style phase to show.
    ///
    /// - `title`: the style's name for the request with this run id (`StyleRequestTitles.prefix` of its input).
    /// - `model`: a block's model id as the phone shows it.
    /// - `tails`: a background run's last output lines by block id (`AppStore.backgroundOutput`).
    /// - `wired`: the requests of the last build, by run id; replaced by this build's.
    public static func make(_ shape: MightyStructure.Shape, runCount: Int, title: (String) -> String? = { _ in nil },
                            model: (String) -> String = { $0 }, tails: [String: [String]] = [:]) -> MobileStructure? {
        var wired: [String: Wired] = [:]
        return make(shape, runCount: runCount, title: title, model: model, tails: tails, wired: &wired)
    }

    public static func make(_ shape: MightyStructure.Shape, runCount: Int, title: (String) -> String? = { _ in nil },
                            model: (String) -> String = { $0 }, tails: [String: [String]] = [:], wired cache: inout [String: Wired]) -> MobileStructure? {
        let all: [MightyStructure.Item]
        switch shape {
        case .phases(let columns):
            guard runCount > 0 || !columns.isEmpty else { return nil }
            all = columns.flatMap(\.items)
        case .free(let rows):
            guard !rows.isEmpty else { return nil }
            all = rows.map(\.item)
        }
        // A run id the pane repeats (a damaged save) travels once, as its newest request.
        var newest: [String: Int] = [:]
        for item in all { newest[item.runID] = max(newest[item.runID] ?? item.runIndex, item.runIndex) }
        let candidates = all.filter { newest[$0.runID] == $0.runIndex }.sorted { $0.runIndex > $1.runIndex }.prefix(maximumItems)
        var next: [String: Wired] = [:]
        let wired = candidates.map { source -> Wired in
            let name = title(source.runID)
            let held = tails.isEmpty ? [:] : itemTails(source, tails)
            if let kept = cache[source.runID], kept.source == source, kept.title == name, kept.tails == held {
                next[source.runID] = kept
                return kept
            }
            let item = wire(source, title: name, model: model, tails: held)
            let made = Wired(source: source, title: name, tails: held, item: item, size: encodedSize(item))
            next[source.runID] = made
            return made
        }
        cache = next

        // Newest first by the estimate, then the real size decides.
        var used = encodedSize(build(shape, kept: [:], newest: newest, omitted: newest.count))
        var count = 0
        for request in wired {
            let cost = request.size + itemOverhead
            if count > 0, used + cost > budgetBytes { break }
            used += cost
            count += 1
        }
        var kept = Dictionary(wired.prefix(count).map { ($0.source.runID, $0.item) }, uniquingKeysWith: { first, _ in first })
        var order = wired.prefix(count).map(\.source.runID)
        var result = build(shape, kept: kept, newest: newest, omitted: newest.count - kept.count)
        while encodedSize(result) > budgetBytes {
            if order.count > 1 {
                kept.removeValue(forKey: order.removeLast())
                result = build(shape, kept: kept, newest: newest, omitted: newest.count - kept.count)
                continue
            }
            // The newest request alone is over: keep as many of its first nodes as fit.
            guard let id = order.first, let item = kept[id] else { break }
            let total = item.clusters.reduce(0) { $0 + $1.nodes.count }
            func fitted(_ nodes: Int) -> MobileStructure {
                var copy = kept
                copy[id] = keeping(item, nodes: nodes)
                return build(shape, kept: copy, newest: newest, omitted: newest.count - copy.count)
            }
            var low = 0, high = total
            while low < high {
                let middle = (low + high + 1) / 2
                if encodedSize(fitted(middle)) <= budgetBytes { low = middle } else { high = middle - 1 }
            }
            result = fitted(low)
            break
        }
        return result
    }

    /// The wire value holding `kept` (by run id), each request once, where the Mac drew it.
    static func build(_ shape: MightyStructure.Shape, kept: [String: Item], newest: [String: Int], omitted: Int) -> MobileStructure {
        func carried(_ item: MightyStructure.Item) -> Item? { newest[item.runID] == item.runIndex ? kept[item.runID] : nil }
        switch shape {
        case .phases(let columns):
            return MobileStructure(shape: "phases", columns: columns.map { column in
                Column(id: column.phase.id, title: capped(column.phase.title, titleBytes, singleLine: true), step: column.step.rawValue,
                       items: column.items.compactMap(carried))
            }, omitted: omitted > 0 ? omitted : nil)
        case .free(let rows):
            return MobileStructure(shape: "free", rows: rows.compactMap { row in
                carried(row.item).map { Row(item: $0, request: row.request.rawValue, execution: row.execution.rawValue, result: row.result.rawValue) }
            }, omitted: omitted > 0 ? omitted : nil)
        }
    }

    /// The lines of `tails` that belong to the request's tasks.
    static func itemTails(_ item: MightyStructure.Item, _ tails: [String: [String]]) -> [String: [String]] {
        var held: [String: [String]] = [:]
        for cluster in item.clusters {
            for node in cluster.nodes where node.kind == "task" { if let lines = tails[node.agentID] { held[node.agentID] = lines } }
        }
        return held
    }

    static func wire(_ item: MightyStructure.Item, title: String?, model: (String) -> String, tails: [String: [String]] = [:]) -> Item {
        var nodes = 0
        var clusters: [Cluster] = []
        for cluster in item.clusters {
            guard nodes < maximumNodes else { break }
            let taken = cluster.nodes.prefix(maximumNodes - nodes)
            nodes += taken.count
            clusters.append(Cluster(nodes: taken.map { node in
                Node(id: node.agentID, depth: min(max(0, node.depth), maximumDepth), title: capped(node.title, titleBytes, singleLine: true),
                     status: node.status, kind: node.kind, model: node.model.map { capped(model($0), modelBytes, singleLine: true) },
                     prompt: node.prompt.map { capped($0.text, promptBytes) }, output: node.output.map { capped($0.text, outputBytes) },
                     activity: node.activity.last.map(activity),
                     background: node.background ? true : nil,
                     outputTail: node.kind == "task" ? MobileMightySupport.outputTail(tails[node.agentID]) : nil,
                     noticeReply: node.noticeReply.map { capped($0.text, outputBytes) })
            }))
        }
        let total = item.clusters.reduce(0) { $0 + $1.nodes.count }
        let named = title.map { capped($0, titleBytes, singleLine: true) }
        return Item(runId: item.runID, ordinal: item.ordinal, title: named?.isEmpty == false ? named : nil, status: item.status,
                    finished: item.finished, request: item.request.map { capped($0.text, requestBytes) },
                    message: item.message.map { capped($0.text, messageBytes) },
                    activity: item.activity.suffix(maximumActivity).map(activity),
                    answer: item.answer.map { capped($0.text, answerBytes, markdown: true) },
                    tally: Tally(agents: item.tally.agents, tools: item.tally.tools), clusters: clusters,
                    omittedNodes: total > nodes ? total - nodes : nil)
    }

    static func activity(_ line: MightyStructure.ActivityLine) -> Activity {
        Activity(kind: capped(line.kind, 40, singleLine: true), state: capped(line.state, 40, singleLine: true),
                 text: capped(line.text, activityBytes, singleLine: true))
    }

    /// The request with only its first `nodes` nodes, depth-first, the rest counted as left out.
    static func keeping(_ item: Item, nodes: Int) -> Item {
        var copy = item
        var left = max(0, nodes)
        var dropped = 0
        copy.clusters = item.clusters.compactMap { cluster in
            let taken = Array(cluster.nodes.prefix(left))
            dropped += cluster.nodes.count - taken.count
            left -= taken.count
            return taken.isEmpty ? nil : Cluster(nodes: taken)
        }
        let omitted = (item.omittedNodes ?? 0) + dropped
        copy.omittedNodes = omitted > 0 ? omitted : nil
        return copy
    }

    /// `text` within `maximumBytes` of UTF-8, cut between characters (a combined emoji is
    /// never split) and marked with "…"; a cut Markdown text has a code fence it leaves
    /// open closed again, the closing line counted inside the cap.
    static func capped(_ text: String, _ maximumBytes: Int, singleLine: Bool = false, markdown: Bool = false) -> String {
        let flat = singleLine ? text.split(whereSeparator: \.isNewline).joined(separator: " ") : text
        guard flat.utf8.count > maximumBytes else { return flat }
        func cut(_ bytes: Int) -> String {
            prefix(flat, bytes: max(0, bytes)).trimmingCharacters(in: .whitespacesAndNewlines) + "…"
        }
        var reserved = 0
        while true {
            let result = cut(maximumBytes - 3 - reserved)
            guard markdown, let fence = MightyStructure.openFence(result) else { return result }
            let closing = 1 + fence.utf8.count
            // A shorter cut can land inside an earlier, longer fence: reserve for that one and cut again.
            if closing <= reserved { return result + "\n" + fence }
            reserved = closing
        }
    }

    /// The longest run of whole characters of `text` within `bytes` of UTF-8.
    static func prefix(_ text: String, bytes: Int) -> String {
        var used = 0
        var end = text.startIndex
        for index in text.indices {
            let size = text[index].utf8.count
            if used + size > bytes { break }
            used += size
            end = text.index(after: index)
        }
        return String(text[..<end])
    }

    private static let encoder = JSONEncoder()
    private static let sortedEncoder: JSONEncoder = {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        return encoder
    }()

    static func encodedSize(_ value: some Encodable) -> Int { (try? encoder.encode(value).count) ?? Int.max / 2 }

    /// The phone echoes this back (`?structure=<tag>`), so an unchanged value is not sent
    /// again: 1…999 999 999 999 from the value's bytes, 0 for no value at all.
    public static func tag(_ value: MobileStructure?) -> Int {
        guard let value, let data = try? sortedEncoder.encode(value) else { return 0 }
        let number = SHA256.hash(data: data).prefix(8).reduce(UInt64(0)) { $0 << 8 | UInt64($1) }
        return Int(number % 999_999_999_999) + 1
    }
}

/// The phone's structure per pane, built again only when what the Mac's structure view
/// reads moved (`MightyStructure.Signature`), and then only the cards of the requests
/// that moved (`MightyStructure.ItemCache`), as the Mac view keeps its own. A streamed
/// token that changes nothing drawn reuses the last value.
public final class MobileStructureCache {
    /// A pane's value and the tag the phone echoes for it.
    public struct Built: Equatable {
        public var value: MobileStructure?
        public var tag: Int
    }
    private struct Entry {
        var signature: MightyStructure.Signature?
        var key = ""
        var tails: [String: [String]] = [:]
        var items = MightyStructure.ItemCache()
        var wired: [String: MobileStructure.Wired] = [:]
        var built = Built(value: nil, tag: 0)
    }
    private var entries: [String: Entry] = [:]
    /// How many times a value was built rather than reused; read by tests.
    public private(set) var builds = 0

    public init() {}

    /// `MightyStructure.make` with the inputs the Mac view passes, turned into the wire
    /// shape. `key` names whatever else the value reads: the styles whose request titles
    /// it carries and the model names it shows. `title` reads a request's input. `tails`: the
    /// pane's background runs' last output lines by block id, which only the host holds.
    public func structure(pane: String, runs: [MightyGraphRun], style: StyleEvaluator?, currentPhase: StylePhase?,
                          planHistory: [PlanRecord] = [], pendingPlanRunID: String? = nil, key: String = "",
                          tails: [String: [String]] = [:],
                          title: (String) -> String? = { _ in nil }, model: (String) -> String = { $0 }) -> Built {
        let signature = MightyStructure.Signature(runs: runs, style: style, currentPhase: currentPhase,
                                                  planHistory: planHistory, pendingPlanRunID: pendingPlanRunID)
        var entry = entries[pane] ?? Entry()
        if entry.signature == signature, entry.key == key, entry.tails == tails { return entry.built }
        // Another key can name each request differently: nothing kept is reused.
        if entry.key != key { entry.wired = [:] }
        let items = entry.items.items(runs, signature: signature)
        let shape = MightyStructure.make(runs: runs, style: style, currentPhase: currentPhase, planHistory: planHistory,
                                         pendingPlanRunID: pendingPlanRunID, items: items)
        let inputs = Dictionary(runs.map { ($0.id, $0.input) }, uniquingKeysWith: { _, last in last })
        let value = MobileStructure.make(shape, runCount: runs.count, title: { inputs[$0].flatMap(title) }, model: model, tails: tails, wired: &entry.wired)
        entry.built = Built(value: value, tag: MobileStructure.tag(value))
        entry.signature = signature
        entry.key = key
        entry.tails = tails
        entries[pane] = entry
        builds += 1
        return entry.built
    }

    /// Forgets the panes not in `panes`.
    public func retain(_ panes: Set<String>) {
        guard entries.keys.contains(where: { !panes.contains($0) }) else { return }
        entries = entries.filter { panes.contains($0.key) }
    }
}
