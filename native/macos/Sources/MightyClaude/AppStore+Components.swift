import AppKit
import MightyCore

/// Settings → 구성 요소: what the app needs on this Mac, with one-click steps.
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
                                   detail: provider.detail + (command.map { " 터미널에서 설치: \($0)" } ?? ""),
                                   actions: command == nil ? [] : [ComponentAction(id: "copy-command", title: "설치 명령 복사")])
        }
        if provider.id == "claude", let mods = runtime?.mods, mods.status == "unsupported" {
            return ComponentStatus(id: provider.id, title: title, state: "attention", version: provider.version,
                                   detail: "Claude Mods(Mighty 모드·펫·모바일 리모트의 실시간 정보)는 Claude Code \(mods.minimumVersion) 이상이 필요합니다.",
                                   actions: [ComponentAction(id: "update", title: "CLI 업데이트")])
        }
        let extra = provider.id == "claude" ? " Mighty bridge Mod는 앱에 내장되어 실행마다 자동으로 연결됩니다." : provider.id == "codex" ? " 플러그인은 마켓플레이스 화면에서 관리합니다." : ""
        return ComponentStatus(id: provider.id, title: title, state: "installed", version: provider.version, detail: "실행 준비됨." + extra, actions: [])
    }

    private func requiredPluginRow(_ plugin: RequiredPlugin) async -> ComponentStatus {
        guard let workspace = snapshot.workspaces.first(where: { $0.remote == nil }) else {
            return ComponentStatus(id: "plugin:" + plugin.id, title: plugin.title, state: "attention", detail: "플러그인 상태를 확인하려면 로컬 워크스페이스가 하나 필요합니다.")
        }
        let snapshotValue = plugin.provider == "codex" ? await codexPlugins.snapshot(workspace: workspace) : await claudePlugins.snapshot(workspace: workspace)
        let installed = snapshotValue.installed.contains { $0.id == plugin.pluginID }
        return ComponentStatus(id: "plugin:" + plugin.id, title: plugin.title, state: installed ? "installed" : "missing",
                               detail: plugin.reason, actions: installed ? [] : [ComponentAction(id: "install-plugin", title: "플러그인 설치")])
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
                    componentMessage = "복사했습니다: " + command
                }
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
