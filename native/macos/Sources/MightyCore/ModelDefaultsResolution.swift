import Foundation

public enum ModelDefaultsResolution {
    /// Resolves the effective model name for a run.
    ///
    /// Priority:
    /// 1. If `sessionModel` is not "default", return it directly.
    /// 2. Look up `permissionMode` in workspace-level defaults.
    /// 3. Look up `permissionMode` in app-level defaults.
    /// 4. Return "default" (CLI decides).
    public static func resolve(
        sessionModel: String,
        provider: String,
        permissionMode: String,
        workspaceDefaults: ModelDefaultsConfig?,
        appDefaults: ModelDefaultsConfig?
    ) -> String {
        guard sessionModel == "default" else { return sessionModel }
        for source in [workspaceDefaults, appDefaults] {
            if let name = modeLookup(config: source, provider: provider, mode: permissionMode), name != "default" {
                return name
            }
        }
        return "default"
    }

    /// The model name shown in the permission-mode menu for a given mode.
    /// Equivalent to resolving with sessionModel = "default".
    public static func modeMenuLabel(
        provider: String,
        permissionMode: String,
        workspaceDefaults: ModelDefaultsConfig?,
        appDefaults: ModelDefaultsConfig?
    ) -> String {
        resolve(
            sessionModel: "default",
            provider: provider,
            permissionMode: permissionMode,
            workspaceDefaults: workspaceDefaults,
            appDefaults: appDefaults
        )
    }

    /// The model label for a graph request node or phone block.
    ///
    /// Priority:
    /// 1. If `cliReportedModel` is non-empty, it is the actual model the CLI used — return it.
    /// 2. If `configuredModel` is not "default", it was set before running — return it with
    ///    the "설정" marker so the user knows this is the configured name, not confirmed by CLI.
    /// 3. Otherwise return nil (no label; CLI decided and we do not know which model it chose).
    public static func nodeModelLabel(cliReportedModel: String?, configuredModel: String) -> String? {
        if let reported = cliReportedModel, !reported.isEmpty { return reported }
        if configuredModel != "default" { return configuredModel + " · 설정" }
        return nil
    }

    private static func modeLookup(config: ModelDefaultsConfig?, provider: String, mode: String) -> String? {
        guard let config else { return nil }
        let defaults = provider == "codex" ? config.codex.modeDefaults : config.claude.modeDefaults
        return defaults[mode]
    }

    /// Removes a registered model name from `config` for `provider`, reverting any mode rows
    /// that referenced that name to "default". Returns the number of mode rows reverted.
    /// Rows already at "default" are not counted.
    @discardableResult
    public static func removeRegisteredModel(name: String, provider: String, from config: inout ModelDefaultsConfig) -> Int {
        var providerDefaults = provider == "codex" ? config.codex : config.claude
        providerDefaults.registeredModels.removeAll { $0.name == name }
        var reverted = 0
        for mode in providerDefaults.modeDefaults.keys where providerDefaults.modeDefaults[mode] == name {
            providerDefaults.modeDefaults[mode] = "default"
            reverted += 1
        }
        if provider == "codex" { config.codex = providerDefaults } else { config.claude = providerDefaults }
        return reverted
    }
}
