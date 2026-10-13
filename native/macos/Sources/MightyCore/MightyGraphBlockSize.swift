import Foundation

/// User-chosen graph card dimensions in canvas points, independent of zoom.
public struct MightyGraphBlockSize: Codable, Sendable, Equatable {
    public var width: Double
    public var height: Double
    public init(width: Double, height: Double) { self.width = width; self.height = height }

    /// The smallest and largest size a block may be dragged to.
    public static let minimumWidth: Double = 300
    public static let minimumHeight: Double = 140
    public static let maximumWidth: Double = 1_400
    public static let maximumHeight: Double = 1_200

    public var normalized: Self? { normalized(minimumHeight: Self.minimumHeight) }

    /// `normalized` for the block `nodeID` names: an answered plan's block may
    /// stay as short as it folds (`MightyGraphLayout.planRecordMinimumSize`).
    public func normalized(nodeID: String) -> Self? {
        normalized(minimumHeight: Self.minimumHeight(nodeID: nodeID))
    }

    private func normalized(minimumHeight: Double) -> Self? {
        guard width.isFinite, height.isFinite else { return nil }
        return Self(width: min(Self.maximumWidth, max(Self.minimumWidth, width)), height: min(Self.maximumHeight, max(minimumHeight, height)))
    }

    /// The least height a saved size of `nodeID` keeps.
    public static func minimumHeight(nodeID: String) -> Double {
        suffix(of: nodeID)?.hasPrefix(MightyGraphLayout.planRecordSuffix) == true ? Double(MightyGraphLayout.planRecordMinimumSize.height) : minimumHeight
    }

    /// The suffix of a `nodeID(runID:suffix:)`, read past its length-prefixed run id.
    static func suffix(of id: String) -> Substring? {
        guard let colon = id.firstIndex(of: ":"), let count = Int(id[..<colon]), count >= 0 else { return nil }
        let utf8 = id.utf8
        guard let separator = utf8.index(id.index(after: colon), offsetBy: count, limitedBy: utf8.endIndex),
              separator < utf8.endIndex, utf8[separator] == UInt8(ascii: ":") else { return nil }
        return id[id.index(after: separator)...]
    }

    /// Length-prefixed run identity keeps run/suffix boundaries unambiguous.
    public static func nodeID(runID: String, suffix: String) -> String {
        "\(runID.utf8.count):\(runID):\(suffix)"
    }

    /// Places in a pane's saved sizes. A request and its 실행 block share one place,
    /// so splitting the request block costs a pane no other block's size.
    public static let maximumSavedSizes = 1_024

    /// The diagram layout a pane's saved sizes are dragged in (`RunSession.graphSizesLayout`).
    /// Before it (nil) the request block held the main agent's records as well, so its
    /// size belongs to the 실행 block that holds them now.
    public static let splitLayout = 1

    /// A pane's saved sizes from before the 실행 split, moved once: a request size
    /// becomes its 실행 block's (unless that block has one) and the request block goes
    /// back to its own, short size.
    public static func splittingExecution(_ values: [String: Self]?) -> [String: Self]? {
        guard var values else { return nil }
        for (id, size) in values where suffix(of: id) == "request" {
            values.removeValue(forKey: id)
            let work = String(id.dropLast("request".count)) + MightyGraphLayout.workSuffix
            if values[work] == nil { values[work] = size }
        }
        return values.isEmpty ? nil : values
    }

    /// Keep only nodes owned by this session, preferring its recent turns:
    /// requests, their 실행 blocks, results, agents and the answered plans. The pending plan card
    /// keeps the pane's one size (`RunSession.graphPlanSize`), not one per run.
    /// Pending input is stable across turns even while its card is hidden.
    static func normalized(_ values: [String: Self]?, runs: [MightyGraphRun]) -> [String: Self]? {
        guard let values, !values.isEmpty else { return nil }
        var result: [String: Self] = [:]
        var places = 0
        /// `ids` share one place: kept together, or not at all.
        func retain(_ ids: String...) {
            let kept = ids.compactMap { id in values[id]?.normalized(nodeID: id).map { (id, $0) } }
            guard places < maximumSavedSizes, !kept.isEmpty else { return }
            for (id, size) in kept { result[id] = size }
            places += 1
        }
        retain("pending-input")
        for run in runs.reversed() {
            retain(nodeID(runID: run.id, suffix: "request"), nodeID(runID: run.id, suffix: MightyGraphLayout.workSuffix))
            if run.settled { retain(nodeID(runID: run.id, suffix: "result")) }
            for agent in run.agents { retain(nodeID(runID: run.id, suffix: "agent:" + agent.id)) }
            // Answered plans stay beside the request for good.
            let records = nodeID(runID: run.id, suffix: MightyGraphLayout.planRecordSuffix)
            for id in values.keys.filter({ $0.hasPrefix(records) }).sorted() { retain(id) }
            if places >= maximumSavedSizes { break }
        }
        return result.isEmpty ? nil : result
    }
}
