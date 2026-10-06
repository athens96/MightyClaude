import AppKit
import MightyCore

/// Settings → Components: what the app needs on this Mac, with one-click steps.
extension AppStore {
    func refreshComponents() async {
        guard !ending else { return }
        componentsRefreshing = true
        defer { componentsRefreshing = false }
        var rows: [ComponentStatus] = []
        for id in ProviderOptions.ids {
            let provider = runtime?.providers?.first { $0.id == id } ?? ProviderOptions.fallbackRuntime(id)
            rows.append(providerRow(provider))
        }
        for plugin in ComponentCatalog.requiredPlugins {
            guard let provider = runtime?.providers?.first(where: { $0.id == plugin.provider }), provider.available else { continue }
            rows.append(await requiredPluginRow(plugin))
        }
        components = rows
    }

    private func providerRow(_ provider: ProviderRuntime) -> ComponentStatus {
        let title = ProviderOptions.label(provider.id)
        guard provider.available else {
            let command = ComponentCatalog.installCommand(provider: provider.id)
            return ComponentStatus(id: provider.id, title: title, state: "missing", version: provider.version,
                                   detail: provider.detail + (command.map { L("settings.components.installInTerminal", ["command": $0]) } ?? ""),
                                   actions: command == nil ? [] : [ComponentAction(id: "copy-command", title: L("settings.components.copyInstallCommand"))])
        }
        if provider.id == "claude", let mods = runtime?.mods, mods.status == "unsupported" {
            return ComponentStatus(id: provider.id, title: title, state: "attention", version: provider.version,
                                   detail: L("settings.components.modsNeedsVersion", ["version": mods.minimumVersion]),
                                   actions: [ComponentAction(id: "update", title: L("settings.components.updateCLI"))])
        }
        let detail = provider.id == "claude" ? L("settings.components.readyClaude") : provider.id == "codex" ? L("settings.components.readyCodex") : L("settings.components.ready")
        return ComponentStatus(id: provider.id, title: title, state: "installed", version: provider.version, detail: detail, actions: [])
    }

    private func requiredPluginRow(_ plugin: RequiredPlugin) async -> ComponentStatus {
        guard let workspace = snapshot.workspaces.first else {
            return ComponentStatus(id: "plugin:" + plugin.id, title: plugin.title, state: "attention", detail: L("settings.components.pluginNeedsWorkspace"))
        }
        let snapshotValue = plugin.provider == "codex" ? await codexPlugins.snapshot(workspace: workspace) : await claudePlugins.snapshot(workspace: workspace)
        let installed = snapshotValue.installed.contains { $0.id == plugin.pluginID }
        return ComponentStatus(id: "plugin:" + plugin.id, title: plugin.title, state: installed ? "installed" : "missing",
                               detail: plugin.reason, actions: installed ? [] : [ComponentAction(id: "install-plugin", title: L("settings.components.installPlugin"))])
    }

    func performComponentAction(component: String, action: String) {
        guard componentAction == nil, !ending else { return }
        componentAction = component + ":" + action
        componentMessage = nil; componentMessageIsError = false
        Task {
            defer { componentAction = nil }
            switch (component, action) {
            case (_, "update"): startCLIUpdates()
            case (let provider, "copy-command"):
                if let command = ComponentCatalog.installCommand(provider: provider) {
                    NSPasteboard.general.clearContents(); NSPasteboard.general.setString(command, forType: .string)
                    componentMessage = L("settings.components.copied", ["command": command])
                }
            case (let id, "install-plugin"):
                guard let plugin = ComponentCatalog.requiredPlugins.first(where: { "plugin:" + $0.id == id }),
                      let workspace = snapshot.workspaces.first else { break }
                let result = plugin.provider == "codex" ? await codexPlugins.install(pluginID: plugin.pluginID, workspace: workspace) : await claudePlugins.install(pluginID: plugin.pluginID, scope: "user", workspace: workspace)
                componentMessage = result.detail
            default: break
            }
            try? await Task.sleep(for: .seconds(1))
            await refreshComponents()
        }
    }
}
