import Foundation
import MightyCore

extension AppStore {
    /// Writes a single Claude or Codex knob value to the machine-wide phase model settings.
    func setPhaseModelKnob(_ keyPath: WritableKeyPath<PhaseModelHardcodedConfig, String>, value: String) {
        var config = snapshot.phaseModels ?? PhaseModelHardcodedConfig()
        config[keyPath: keyPath] = value
        snapshot.phaseModels = config
    }

    /// Applies `value` to every Claude single-model knob mapped to `phase`.
    func applyClaudePhaseRow(_ phase: PhaseModelRouting.Phase, value: String) {
        var h = snapshot.phaseModels ?? PhaseModelHardcodedConfig()
        var c = h.toPhaseModelConfig(omcAgents: nil, ouroborosKeys: nil)
        PhaseModelRouting.applyClaudeRow(phase: phase, value: value, to: &c)
        h.claudeMain = c.claudeMain
        h.claudeOpusAlias = c.claudeOpusAlias
        h.claudeSonnetAlias = c.claudeSonnetAlias
        h.claudeHaikuAlias = c.claudeHaikuAlias
        h.claudeSubagentDefault = c.claudeSubagentDefault
        snapshot.phaseModels = h
    }

    /// Applies `value` to every Codex single-model knob mapped to `phase`.
    func applyCodexPhaseRow(_ phase: PhaseModelRouting.Phase, value: String) {
        var h = snapshot.phaseModels ?? PhaseModelHardcodedConfig()
        var c = h.toPhaseModelConfig(omcAgents: nil, ouroborosKeys: nil)
        PhaseModelRouting.applyCodexRow(phase: phase, value: value, to: &c)
        h.codexReviewModel = c.codexReviewModel
        h.codexSubagentDefault = c.codexSubagentDefault
        snapshot.phaseModels = h
    }

    func addRegisteredModel(provider: String, entry: RegisteredModelEntry) {
        var config = snapshot.modelDefaults ?? ModelDefaultsConfig()
        if provider == "codex" {
            guard !config.codex.registeredModels.contains(where: { $0.name == entry.name }) else { return }
            config.codex.registeredModels.append(entry)
        } else {
            guard !config.claude.registeredModels.contains(where: { $0.name == entry.name }) else { return }
            config.claude.registeredModels.append(entry)
        }
        snapshot.modelDefaults = config
    }

    func removeRegisteredModel(provider: String, name: String) {
        guard var config = snapshot.modelDefaults else { return }
        config.claude.registeredModels.removeAll { $0.name == name }
        config.codex.registeredModels.removeAll { $0.name == name }
        snapshot.modelDefaults = config
    }
}
