import Foundation
import MightyCore

extension AppStore {
    func setAppModeDefault(provider: String, mode: String, model: String) {
        var config = snapshot.modelDefaults ?? ModelDefaultsConfig()
        if provider == "codex" { config.codex.modeDefaults[mode] = model }
        else { config.claude.modeDefaults[mode] = model }
        snapshot.modelDefaults = config
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
        ModelDefaultsResolution.removeRegisteredModel(name: name, provider: provider, from: &config)
        snapshot.modelDefaults = config
    }
}
