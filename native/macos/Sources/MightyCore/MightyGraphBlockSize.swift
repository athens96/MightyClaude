import Foundation

/// User-chosen graph card dimensions in canvas points, independent of zoom.
public struct MightyGraphBlockSize: Codable, Sendable, Equatable {
    public var width: Double
    public var height: Double
    public init(width: Double, height: Double) { self.width = width; self.height = height }

    public var normalized: Self? {
        guard width.isFinite, height.isFinite else { return nil }
        return Self(width: min(1_400, max(300, width)), height: min(1_200, max(140, height)))
    }

    /// Length-prefixed run identity keeps run/suffix boundaries unambiguous.
    public static func nodeID(runID: String, suffix: String) -> String {
        "\(runID.utf8.count):\(runID):\(suffix)"
    }

    public static let maximumSavedSizes = 1_024

    /// Keep only nodes owned by this session, preferring its recent turns.
    /// Pending input is stable across turns even while its card is hidden.
    static func normalized(_ values: [String: Self]?, runs: [MightyGraphRun]) -> [String: Self]? {
        guard let values, !values.isEmpty else { return nil }
        var result: [String: Self] = [:]
        func retain(_ id: String) {
            guard result.count < maximumSavedSizes, let size = values[id]?.normalized else { return }
            result[id] = size
        }
        retain("pending-input")
        for run in runs.reversed() {
            retain(nodeID(runID: run.id, suffix: "request"))
            if run.settled { retain(nodeID(runID: run.id, suffix: "result")) }
            for agent in run.agents { retain(nodeID(runID: run.id, suffix: "agent:" + agent.id)) }
            if result.count >= maximumSavedSizes { break }
        }
        return result.isEmpty ? nil : result
    }
}
