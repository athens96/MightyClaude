import Foundation

/// Formats model and token information for the macOS execution graph.
/// All user-visible copy goes through L() so no Korean literals appear here.
public enum ModelUsageFormat {
    /// Per-model token totals derived from response records, in first-seen order.
    public static func blockModels(records: [GraphResponseRecord]) -> [(model: String, usage: GraphTokenUsage)] {
        var usages: [String: GraphTokenUsage] = [:]
        var order: [String] = []
        for record in records {
            let key = record.model ?? ""
            if usages[key] == nil { order.append(key) }
            usages[key] = (usages[key] ?? GraphTokenUsage()) + record.usage
        }
        return order.compactMap { key in usages[key].map { (key, $0) } }
    }

    /// Block capsule text shown on the block header.
    /// - Before the first response: returns nodeModelLabel (the configured label, which
    ///   already carries the graph.nodeModel.configuredSuffix marker).
    /// - After responses: "12.3K · Model" or "12.3K · Model +N" for multiple models.
    /// - Returns nil when there is nothing meaningful to display.
    public static func blockCapsule(
        usage: GraphTokenUsage?,
        records: [GraphResponseRecord],
        nodeModelLabel: String?,
        catalog: [ModelOption] = []
    ) -> String? {
        if records.isEmpty { return nodeModelLabel }
        guard let usage, !usage.isEmpty else { return nodeModelLabel }
        let tokenStr = GraphTokenUsage.compact(usage.total)
        let models = blockModels(records: records).filter { !$0.model.isEmpty }
        guard !models.isEmpty else { return tokenStr }
        let first = shortName(models[0].model, catalog: catalog)
        return models.count == 1 ? "\(tokenStr) · \(first)" : "\(tokenStr) · \(first) +\(models.count - 1)"
    }

    /// Tooltip for the block capsule: per-model token breakdown with input/output/cache detail.
    public static func blockCapsuleHelp(
        records: [GraphResponseRecord],
        catalog: [ModelOption] = []
    ) -> String {
        blockModels(records: records)
            .filter { !$0.model.isEmpty }
            .map { "\(shortName($0.model, catalog: catalog)) · \($0.usage.detail)" }
            .joined(separator: "\n")
    }

    /// Suffix appended to an activity line.
    /// - Task/Agent line: caller model and tokens (first-line rule) followed by
    ///   the subagent prefix and the child block's capsule text.
    /// - First activity of a response: "ModelName · tokens".
    /// - Later activity of the same response: the "same response" locale marker.
    /// - Not found in any response: nil.
    public static func activitySuffix(
        activityId: String,
        records: [GraphResponseRecord],
        childBlock: GraphChildBlock?,
        catalog: [ModelOption] = []
    ) -> String? {
        for record in records {
            guard let index = record.activityIds.firstIndex(of: activityId) else { continue }
            let callerPart: String
            if index == 0 {
                let tokenStr = GraphTokenUsage.compact(record.usage.total)
                if let model = record.model, !model.isEmpty {
                    callerPart = "\(shortName(model, catalog: catalog)) · \(tokenStr)"
                } else {
                    callerPart = tokenStr
                }
            } else {
                callerPart = L("usage.modelUsage.sameResponse")
            }
            if let child = childBlock,
               let childCapsule = blockCapsule(usage: child.usage, records: child.records, nodeModelLabel: nil, catalog: catalog) {
                return "\(callerPart) · \(L("usage.modelUsage.subagentPrefix")) \(childCapsule)"
            }
            return callerPart
        }
        if let child = childBlock,
           let childCapsule = blockCapsule(usage: child.usage, records: child.records, nodeModelLabel: nil, catalog: catalog) {
            return "\(L("usage.modelUsage.subagentPrefix")) \(childCapsule)"
        }
        return nil
    }

    /// The tokens the calling response attributes to this activity line —
    /// the caller's own response total when this is the first activity of that
    /// response, nil for same-response lines and unknown activity IDs.
    public static func callerAttribution(activityId: String, records: [GraphResponseRecord]) -> Int? {
        for record in records {
            guard let index = record.activityIds.firstIndex(of: activityId) else { continue }
            return index == 0 ? record.usage.total : nil
        }
        return nil
    }

    /// Short display name for a model ID looked up against the catalog.
    public static func shortName(_ modelId: String, catalog: [ModelOption] = []) -> String {
        catalog.first { $0.value == modelId || $0.resolvedModel == modelId }?.displayName ?? modelId
    }
}
