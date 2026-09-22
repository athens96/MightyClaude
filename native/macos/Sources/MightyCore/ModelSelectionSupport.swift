import Foundation

public struct ModelSelectionResolution: Sendable, Equatable {
    public var model: String
    public var effort: String
    public init(model: String, effort: String) { self.model = model; self.effort = effort }
}

public enum ModelSelectionSupport {
    /// Only successful CLI metadata can retire a previously offered model.
    /// Custom IDs survive missing or inconclusive discovery results.
    public static func reconcile(model: String, effort: String, previous: ModelCatalog?, refreshed: ModelCatalog, provider: String? = nil) -> ModelSelectionResolution {
        let original = ModelSelectionResolution(model: model, effort: effort)
        guard refreshed.source == "cli" else { return original }
        let prior = previous?.models.first { $0.value == model || $0.resolvedModel == model }
        let directMatch = refreshed.models.first { $0.value == model }
            ?? refreshed.models.first { $0.value != "default" && $0.resolvedModel == model }
        // Bedrock metadata can list concrete regional IDs while the CLI still
        // accepts stable aliases. Missing metadata must not change their model
        // family (or freeze an alias to its previous concrete resolution).
        let stableClaudeAlias = provider == "claude" && !model.hasPrefix("claude-") && CoreValidation.isOfficialClaudeModel(model)
        if stableClaudeAlias && directMatch == nil { return original }
        let matched = directMatch ?? prior?.resolvedModel.flatMap { resolved in
                refreshed.models.first { $0.value != "default" && ($0.value == resolved || $0.resolvedModel == resolved) }
            }
        guard let matched else {
            let retiredOfficial = provider == "claude" && model.hasPrefix("claude-") && CoreValidation.isOfficialClaudeModel(model)
            return prior == nil && !retiredOfficial ? original : ModelSelectionResolution(model: "default", effort: "default")
        }
        let unsupported = matched.supportsEffort == false
            || matched.supportedEffortLevels.map { !$0.contains(effort) } == true
        return ModelSelectionResolution(model: matched.value, effort: effort != "default" && unsupported ? "default" : effort)
    }
}
