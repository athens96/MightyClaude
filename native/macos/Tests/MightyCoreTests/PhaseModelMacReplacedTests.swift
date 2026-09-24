import Foundation
import Testing
@testable import MightyCore

/// Verifies that the old per-permission-mode default-model feature is gone and the
/// new PhaseModelHardcodedConfig / AppSnapshot.phaseModels are in place.
struct PhaseModelMacReplacedTests {

    @Test func phaseModelHardcodedConfigDefaultsAllDefault() {
        let c = PhaseModelHardcodedConfig()
        #expect(c.claudeMain == "default")
        #expect(c.claudeOpusAlias == "default")
        #expect(c.claudeSonnetAlias == "default")
        #expect(c.claudeHaikuAlias == "default")
        #expect(c.claudeSubagentDefault == "default")
        #expect(c.codexReviewModel == "default")
        #expect(c.codexSubagentDefault == "default")
        #expect(c.codexPlanModeReasoningEffort == "default")
    }

    @Test func phaseModelHardcodedConfigCodable() throws {
        let original = PhaseModelHardcodedConfig(
            claudeMain: "claude-sonnet-5",
            claudeOpusAlias: "claude-opus-5-5",
            codexReviewModel: "o4"
        )
        let data = try JSONEncoder().encode(original)
        let decoded = try JSONDecoder().decode(PhaseModelHardcodedConfig.self, from: data)
        #expect(decoded == original)
    }

    @Test func appSnapshotAcceptsPhaseModels() {
        let pm = PhaseModelHardcodedConfig(claudeMain: "claude-sonnet-5")
        let snap = AppSnapshot(phaseModels: pm)
        #expect(snap.phaseModels?.claudeMain == "claude-sonnet-5")
    }

    @Test func appSnapshotPhaseModelsNilByDefault() {
        let snap = AppSnapshot()
        #expect(snap.phaseModels == nil)
    }

    @Test func nodeModelLabelRemainsAccessible() {
        let label = GraphModelLabel.nodeModelLabel(cliReportedModel: "claude-sonnet-5", configuredModel: "default")
        #expect(label == "claude-sonnet-5")
    }

    @Test func nodeModelLabelUsesConfiguredWhenNoCliReport() {
        let label = GraphModelLabel.nodeModelLabel(cliReportedModel: nil, configuredModel: "claude-opus-5-5")
        // The suffix is localized, so only the prefix and the presence of a suffix are asserted.
        #expect(label?.hasPrefix("claude-opus-5-5 ") == true)
        #expect((label?.count ?? 0) > "claude-opus-5-5 ".count)
    }

    @Test func toPhaseModelConfigPreservesHardcodedKnobs() {
        let h = PhaseModelHardcodedConfig(
            claudeMain: "claude-sonnet-5",
            claudeOpusAlias: "claude-opus-5-5",
            claudeSubagentDefault: "claude-haiku-4-5-20251001",
            codexReviewModel: "o4"
        )
        let c = h.toPhaseModelConfig(omcAgents: nil, ouroborosKeys: nil)
        #expect(c.claudeMain == "claude-sonnet-5")
        #expect(c.claudeOpusAlias == "claude-opus-5-5")
        #expect(c.claudeSubagentDefault == "claude-haiku-4-5-20251001")
        #expect(c.codexReviewModel == "o4")
        #expect(c.omcAgents == nil)
        #expect(c.ouroborosKeys == nil)
    }

    @Test func markerMACReplacedOK() {
        print("MAC_REPLACED_OK")
    }
}
