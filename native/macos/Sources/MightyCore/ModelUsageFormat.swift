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
        catalog: [ModelOption] = [],
        versioned: Bool = false
    ) -> String? {
        let nodeModelLabel = versioned ? versionedNodeLabel(nodeModelLabel) : nodeModelLabel
        if records.isEmpty { return nodeModelLabel }
        guard let usage, !usage.isEmpty else { return nodeModelLabel }
        let tokenStr = GraphTokenUsage.compact(usage.total)
        let models = blockModels(records: records).filter { !$0.model.isEmpty }
        guard !models.isEmpty else { return tokenStr }
        let first = shortName(models[0].model, catalog: catalog, versioned: versioned)
        return models.count == 1 ? "\(tokenStr) · \(first)" : "\(tokenStr) · \(first) +\(models.count - 1)"
    }

    /// Tooltip for the block capsule: per-model token breakdown with input/output/cache detail.
    public static func blockCapsuleHelp(
        records: [GraphResponseRecord],
        catalog: [ModelOption] = [],
        versioned: Bool = false
    ) -> String {
        blockModels(records: records)
            .filter { !$0.model.isEmpty }
            .map { "\(shortName($0.model, catalog: catalog, versioned: versioned)) · \($0.usage.detail)" }
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
        catalog: [ModelOption] = [],
        versioned: Bool = false
    ) -> String? {
        for record in records {
            guard record.activityIds.contains(activityId) else { continue }
            let callerPart: String
            // The number drawn is exactly the attribution the sum rule counts.
            if let attributed = callerAttribution(activityId: activityId, records: [record]) {
                let tokenStr = GraphTokenUsage.compact(attributed)
                if let model = record.model, !model.isEmpty {
                    callerPart = "\(shortName(model, catalog: catalog, versioned: versioned)) · \(tokenStr)"
                } else {
                    callerPart = tokenStr
                }
            } else {
                callerPart = L("usage.modelUsage.sameResponse")
            }
            if let child = childBlock,
               let childCapsule = blockCapsule(usage: child.usage, records: child.records, nodeModelLabel: nil, catalog: catalog, versioned: versioned) {
                let sub = L("usage.modelUsage.subagentPrefix")
                return "\(callerPart) · \(sub) \(childCapsule)"
            }
            return callerPart
        }
        if let child = childBlock,
           let childCapsule = blockCapsule(usage: child.usage, records: child.records, nodeModelLabel: nil, catalog: catalog, versioned: versioned) {
            let sub = L("usage.modelUsage.subagentPrefix")
            return "\(sub) \(childCapsule)"
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

    /// A saved `GraphModelLabel.nodeModelLabel` (`claude-opus-4-5` or
    /// `opus · 설정`) with its model part read through `ModelLabel`. A configured
    /// alias stays the bare family (`Opus · 설정`): today's catalogue does not
    /// say which version an older run used.
    public static func versionedNodeLabel(_ label: String?) -> String? {
        guard let label else { return nil }
        let marker = " " + L("graph.nodeModel.configuredSuffix")
        let configured = label.hasSuffix(marker)
        let text = ModelLabel.text(configured ? String(label.dropLast(marker.count)) : label)
        return configured ? text + marker : text
    }

    /// Short display name for a model ID looked up against the catalog.
    /// `versioned` (the Mac views) reads the id with `ModelLabel` first —
    /// `claude-opus-5-5` → `Opus 5.5`; the shared graph vectors keep the
    /// catalogue-only name, which the Windows port also follows.
    public static func shortName(_ modelId: String, catalog: [ModelOption] = [], versioned: Bool = false) -> String {
        if versioned, let label = ModelLabel.format(modelId) { return label }
        guard let option = catalog.first(where: { $0.value == modelId || $0.resolvedModel == modelId }) else { return versioned ? ModelLabel.text(modelId) : modelId }
        return versioned ? ModelLabel.option(option) : option.displayName
    }
}
