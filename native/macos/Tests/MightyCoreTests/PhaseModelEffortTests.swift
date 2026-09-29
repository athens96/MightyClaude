import Foundation
import Testing
@testable import MightyCore

/// Settings > Models reaches real runs, and effort goes only where a CLI
/// takes one and the model the run uses accepts that level.
@Suite struct PhaseModelEffortTests {
    private static let plugin = URL(fileURLWithPath: "/tmp/mods/mighty-bridge")
    private static func req(provider: String = "claude", model: String = "default", phase: PhaseModelConfig) -> StartRunRequest {
        var request = StartRunRequest(sessionId: "s", workspaceId: "w", input: "hi", model: model, provider: provider, settings: RunSettings())
        request.phaseModels = phase
        return request
    }

    @Test func theRequestsOwnPhaseModelsReachTheArguments() throws {
        let claude = try ProviderService.arguments(Self.req(phase: PhaseModelConfig(claudeMain: "claude-opus-5-5")), pluginDirectory: Self.plugin)
        let model = try #require(claude.firstIndex(of: "--model"))
        #expect(claude[model + 1] == "claude-opus-5-5")
        let codex = try ProviderService.arguments(Self.req(provider: "codex", phase: PhaseModelConfig(codexSubagentDefault: "gpt-6-astra", codexSubagentEffort: "high")), pluginDirectory: Self.plugin)
        #expect(codex.contains("agents.default_subagent_model=\"gpt-6-astra\""))
        #expect(codex.contains("agents.default_subagent_reasoning_effort=\"high\""))
        // An explicit argument still wins over the request's.
        let explicit = try ProviderService.arguments(Self.req(phase: PhaseModelConfig(claudeMain: "claude-opus-5-5")), pluginDirectory: Self.plugin, phaseModels: PhaseModelConfig())
        #expect(!explicit.contains("--model"))
    }

    @Test func thePanesOwnEffortWinsAndThePhaseOneFillsTheDefault() {
        let config = PhaseModelConfig(claudeMainEffort: "high", codexMainEffort: "low")
        #expect(config.runEffort(provider: "claude", paneEffort: "max", paneModel: "opus", catalog: nil, registeredModels: []) == "max")
        #expect(config.runEffort(provider: "claude", paneEffort: "default", paneModel: "opus", catalog: nil, registeredModels: []) == "high")
        // The run's model is the phase main model when the pane has none.
        #expect(config.runEffort(provider: "claude", paneEffort: "default", paneModel: "default", catalog: nil, registeredModels: []) == "high")
        let codexCatalog = ModelCatalog(models: [ModelOption(value: "gpt-6-astra", displayName: "Astra", supportsEffort: true, supportedEffortLevels: ["low", "medium", "high"])])
        #expect(config.runEffort(provider: "codex", paneEffort: "default", paneModel: "gpt-6-astra", catalog: codexCatalog, registeredModels: []) == "low")
    }

    @Test func aModelThatTakesNoSuchLevelKeepsTheCLIDefault() {
        let config = PhaseModelConfig(claudeMain: "haiku", claudeMainEffort: "high")
        #expect(config.runEffort(provider: "claude", paneEffort: "default", paneModel: "haiku", catalog: nil, registeredModels: []) == "default")
        #expect(config.runEffort(provider: "claude", paneEffort: "default", paneModel: "default", catalog: nil, registeredModels: []) == "default")
        #expect(PhaseModelConfig().runEffort(provider: "claude", paneEffort: "default", paneModel: "opus", catalog: nil, registeredModels: []) == "default")
    }

    @Test func aStateSavedBeforeTheEffortsStillLoads() throws {
        let old = #"{"claudeMain":"opus","claudeOpusAlias":"default","claudeSonnetAlias":"default","claudeHaikuAlias":"default","claudeSubagentDefault":"default","codexReviewModel":"default","codexSubagentDefault":"default","codexPlanModeReasoningEffort":"high"}"#
        let config = try JSONDecoder().decode(PhaseModelHardcodedConfig.self, from: Data(old.utf8))
        #expect(config.claudeMain == "opus" && config.claudeMainEffort == nil)
        let phase = config.toPhaseModelConfig(omcAgents: nil, ouroborosKeys: nil)
        #expect(phase.claudeMainEffort == "default" && phase.codexSubagentEffort == "default" && phase.codexPlanModeReasoningEffort == "high")
    }
}
