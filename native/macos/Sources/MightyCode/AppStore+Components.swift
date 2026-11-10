import Foundation
import MightyCore

/// Settings → Components: the plugins the app requires for an installed agent.
/// The agent CLIs and other tools are the setup screen's (AppStore+Setup.swift).
extension AppStore {
    func refreshComponents() async {
        guard !ending else { return }
        componentsRefreshing = true
        defer { componentsRefreshing = false }
        var rows: [ComponentStatus] = []
        // Plugin rows ask the CLI; the help capture's demo CLIs have none to ask.
        for plugin in ComponentCatalog.requiredPlugins where !helpCapturing {
            guard let provider = runtime?.providers?.first(where: { $0.id == plugin.provider }), provider.available else { continue }
            rows.append(await requiredPluginRow(plugin))
        }
        components = rows
    }

    private func requiredPluginRow(_ plugin: RequiredPlugin) async -> ComponentStatus {
        guard let workspace = snapshot.workspaces.first(where: { $0.remote == nil }) else {
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
            case (let id, "install-plugin"):
                guard let plugin = ComponentCatalog.requiredPlugins.first(where: { "plugin:" + $0.id == id }),
                      let workspace = snapshot.workspaces.first(where: { $0.remote == nil }) else { break }
                let result = plugin.provider == "codex" ? await codexPlugins.install(pluginID: plugin.pluginID, workspace: workspace) : await claudePlugins.install(pluginID: plugin.pluginID, scope: "user", workspace: workspace)
                componentMessage = result.detail
            default: break
            }
            try? await Task.sleep(for: .seconds(1))
            await refreshComponents()
        }
    }
}
