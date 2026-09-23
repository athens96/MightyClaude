import Foundation

/// Child block data for a Task/Agent activity line: the subagent's usage and responses.
public struct GraphChildBlock: Sendable, Equatable {
    public var usage: GraphTokenUsage?
    public var records: [GraphResponseRecord]
    public init(usage: GraphTokenUsage? = nil, records: [GraphResponseRecord] = []) {
        self.usage = usage; self.records = records
    }
}

/// Maps Task/Agent activity IDs to their subagent block data.
/// Works for the request block and for agent blocks at any nesting depth.
public enum GraphChildBlocks {
    /// Build a child-block map from a set of response records and the full agent list.
    /// For each activity ID in the records, looks up the corresponding child agent by
    /// computing its node ID from the run ID and tool-use ID.
    public static func map(
        responseRecords: [GraphResponseRecord]?,
        agents: [MightyGraphAgent],
        runId: String
    ) -> [String: GraphChildBlock] {
        guard let records = responseRecords else { return [:] }
        var result: [String: GraphChildBlock] = [:]
        for actId in records.flatMap(\.activityIds) {
            let nodeId = ExecutionGraphSupport.agentNodeID(runId: runId, toolUseId: actId)
            guard let agent = agents.first(where: { $0.id == nodeId }) else { continue }
            result[actId] = GraphChildBlock(usage: agent.usage, records: agent.responseRecords ?? [])
        }
        return result
    }
}
