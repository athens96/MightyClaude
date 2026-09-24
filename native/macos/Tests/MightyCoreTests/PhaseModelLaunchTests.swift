import Foundation
import Testing
@testable import MightyCore

struct PhaseModelLaunchTests {

    private static let plugin = URL(fileURLWithPath: "/tmp/mods/mighty-bridge")
    private static func req(provider: String = "claude", model: String = "default", permissionMode: String = "manual", effort: String = "default") -> StartRunRequest {
        StartRunRequest(sessionId: "s", workspaceId: "w", input: "hi", model: model, provider: provider, settings: RunSettings(effort: effort, permissionMode: permissionMode))
    }
    private func settingsEnv(_ args: [String]) throws -> [String: String] {
        let idx = try #require(args.firstIndex(of: "--settings"), "expected --settings flag")
        let obj = try JSONSerialization.jsonObject(with: Data(args[idx + 1].utf8)) as? [String: [String: String]]
        return try #require(obj?["env"])
    }

    // MARK: - Claude: claudeMain drives --model

    @Test func claudeMainSetsModel() throws {
        let args = try ProviderService.arguments(Self.req(), pluginDirectory: Self.plugin,
            phaseModels: PhaseModelConfig(claudeMain: "claude-opus-5-5"))
        #expect(args.contains("--model"))
        let idx = try #require(args.firstIndex(of: "--model"))
        #expect(args[idx + 1] == "claude-opus-5-5")
    }

    @Test func claudeSessionModelWinsOverPhaseMain() throws {
        let args = try ProviderService.arguments(Self.req(model: "claude-sonnet-5"), pluginDirectory: Self.plugin,
            phaseModels: PhaseModelConfig(claudeMain: "claude-opus-5-5"))
        let idx = try #require(args.firstIndex(of: "--model"))
        #expect(args[idx + 1] == "claude-sonnet-5")
    }

    @Test func claudeDefaultKnobsAddNoModelFlag() throws {
        let args = try ProviderService.arguments(Self.req(), pluginDirectory: Self.plugin,
            phaseModels: PhaseModelConfig())
        #expect(!args.contains("--model"))
    }

    // MARK: - Claude: alias pins in --settings env JSON

    @Test func claudeOpusAliasInSettingsEnv() throws {
        let args = try ProviderService.arguments(Self.req(), pluginDirectory: Self.plugin,
            phaseModels: PhaseModelConfig(claudeOpusAlias: "claude-opus-5-5"))
        let env = try settingsEnv(args)
        #expect(env["ANTHROPIC_DEFAULT_OPUS_MODEL"] == "claude-opus-5-5")
        #expect(env["ANTHROPIC_DEFAULT_SONNET_MODEL"] == nil)
        #expect(env["ANTHROPIC_DEFAULT_HAIKU_MODEL"] == nil)
        #expect(env["CLAUDE_CODE_SUBAGENT_MODEL"] == nil)
    }

    @Test func claudeSonnetAliasInSettingsEnv() throws {
        let args = try ProviderService.arguments(Self.req(), pluginDirectory: Self.plugin,
            phaseModels: PhaseModelConfig(claudeSonnetAlias: "claude-sonnet-5"))
        let env = try settingsEnv(args)
        #expect(env["ANTHROPIC_DEFAULT_SONNET_MODEL"] == "claude-sonnet-5")
    }

    @Test func claudeHaikuAliasInSettingsEnv() throws {
        let args = try ProviderService.arguments(Self.req(), pluginDirectory: Self.plugin,
            phaseModels: PhaseModelConfig(claudeHaikuAlias: "claude-haiku-4-5-20251001"))
        let env = try settingsEnv(args)
        #expect(env["ANTHROPIC_DEFAULT_HAIKU_MODEL"] == "claude-haiku-4-5-20251001")
    }

    @Test func claudeSubagentDefaultInSettingsEnv() throws {
        let args = try ProviderService.arguments(Self.req(), pluginDirectory: Self.plugin,
            phaseModels: PhaseModelConfig(claudeSubagentDefault: "claude-haiku-4-5-20251001"))
        let env = try settingsEnv(args)
        #expect(env["CLAUDE_CODE_SUBAGENT_MODEL"] == "claude-haiku-4-5-20251001")
    }

    @Test func claudeAllAliasesInSettingsEnvTogether() throws {
        let config = PhaseModelConfig(
            claudeOpusAlias: "claude-opus-5-5",
            claudeSonnetAlias: "claude-sonnet-5",
            claudeHaikuAlias: "claude-haiku-4-5-20251001",
            claudeSubagentDefault: "claude-haiku-4-5-20251001"
        )
        let args = try ProviderService.arguments(Self.req(), pluginDirectory: Self.plugin, phaseModels: config)
        let env = try settingsEnv(args)
        #expect(env["ANTHROPIC_DEFAULT_OPUS_MODEL"] == "claude-opus-5-5")
        #expect(env["ANTHROPIC_DEFAULT_SONNET_MODEL"] == "claude-sonnet-5")
        #expect(env["ANTHROPIC_DEFAULT_HAIKU_MODEL"] == "claude-haiku-4-5-20251001")
        #expect(env["CLAUDE_CODE_SUBAGENT_MODEL"] == "claude-haiku-4-5-20251001")
    }

    @Test func claudeEffortAndAliasesMergedIntoOneSettingsArg() throws {
        let config = PhaseModelConfig(claudeOpusAlias: "claude-opus-5-5")
        let args = try ProviderService.arguments(Self.req(effort: "high"), pluginDirectory: Self.plugin, phaseModels: config)
        let env = try settingsEnv(args)
        #expect(env["CLAUDE_CODE_EFFORT_LEVEL"] == "high")
        #expect(env["ANTHROPIC_DEFAULT_OPUS_MODEL"] == "claude-opus-5-5")
        // Only one --settings flag
        #expect(args.filter { $0 == "--settings" }.count == 1)
    }

    @Test func claudeDefaultKnobsAddNoSettingsArg() throws {
        let args = try ProviderService.arguments(Self.req(), pluginDirectory: Self.plugin,
            phaseModels: PhaseModelConfig())
        #expect(!args.contains("--settings"))
    }

    // MARK: - Codex: phase model -c args

    @Test func codexReviewModelAdded() throws {
        let args = try ProviderService.arguments(Self.req(provider: "codex"), pluginDirectory: Self.plugin,
            phaseModels: PhaseModelConfig(codexReviewModel: "o4"))
        #expect(args.contains("review_model=\"o4\""))
    }

    @Test func codexSubagentDefaultAdded() throws {
        let args = try ProviderService.arguments(Self.req(provider: "codex"), pluginDirectory: Self.plugin,
            phaseModels: PhaseModelConfig(codexSubagentDefault: "o4-mini"))
        #expect(args.contains("agents.default_subagent_model=\"o4-mini\""))
    }

    @Test func codexPlanModeReasoningEffortAdded() throws {
        let args = try ProviderService.arguments(Self.req(provider: "codex"), pluginDirectory: Self.plugin,
            phaseModels: PhaseModelConfig(codexPlanModeReasoningEffort: "high"))
        #expect(args.contains("plan_mode_reasoning_effort=\"high\""))
    }

    @Test func codexDefaultKnobsAddNoCArgs() throws {
        let args = try ProviderService.arguments(Self.req(provider: "codex"), pluginDirectory: Self.plugin,
            phaseModels: PhaseModelConfig())
        #expect(!args.contains("review_model=\"default\""))
        #expect(!args.contains("agents.default_subagent_model=\"default\""))
        #expect(!args.contains("plan_mode_reasoning_effort=\"default\""))
    }

    @Test func codexAllThreeKnobsTogether() throws {
        let config = PhaseModelConfig(codexReviewModel: "o4", codexSubagentDefault: "o4-mini", codexPlanModeReasoningEffort: "low")
        let args = try ProviderService.arguments(Self.req(provider: "codex"), pluginDirectory: Self.plugin, phaseModels: config)
        #expect(args.contains("review_model=\"o4\""))
        #expect(args.contains("agents.default_subagent_model=\"o4-mini\""))
        #expect(args.contains("plan_mode_reasoning_effort=\"low\""))
    }

    @Test func codexPhaseKnobsAppearsInAppServerPath() throws {
        let config = PhaseModelConfig(codexReviewModel: "o4", codexSubagentDefault: "o4-mini")
        let args = try ProviderService.arguments(
            Self.req(provider: "codex", permissionMode: "onRequest"), pluginDirectory: Self.plugin,
            allowPermissionPrompts: true, phaseModels: config)
        #expect(args.contains("review_model=\"o4\""))
        #expect(args.contains("agents.default_subagent_model=\"o4-mini\""))
        #expect(args.contains("app-server"))
    }
}
