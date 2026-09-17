import AppKit
import MightyCore

/// Settings → 구성 요소: what the app needs on this Mac, with one-click steps.
extension AppStore {
    func refreshComponents() async {
        guard !ending else { return }
        componentsRefreshing = true
        defer { componentsRefreshing = false }
        var rows: [ComponentStatus] = []
        let tailscale = await tailscaleInstaller.inspect()
        lastTailscaleInspection = tailscale
        rows.append(Self.tailscaleRow(tailscale))
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

    private static func tailscaleRow(_ inspection: TailscaleInspection) -> ComponentStatus {
        let actions: [ComponentAction]
        let state: String
        switch inspection.phase {
        case "missing":
            state = "missing"
            actions = inspection.brewAvailable ? [ComponentAction(id: "install", title: "Homebrew로 설치"), ComponentAction(id: "open-store", title: "App Store에서 설치", primary: false)]
                                               : [ComponentAction(id: "open-store", title: "App Store에서 설치")]
        case "needs-launch": state = "attention"; actions = [ComponentAction(id: "launch", title: "Tailscale 실행")]
        case "needs-login": state = "attention"; actions = [ComponentAction(id: "login", title: "로그인"), ComponentAction(id: "launch", title: "앱 열기", primary: false)]
        case "needs-connect": state = "attention"; actions = [ComponentAction(id: "connect", title: "연결")]
        default: state = "installed"; actions = []
        }
        return ComponentStatus(id: "tailscale", title: "Tailscale", state: state, version: inspection.version, detail: inspection.detail + " 원격 워크스페이스와 모바일 리모트에 필요합니다.", actions: actions)
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
        componentMessage = nil
        Task {
            defer { componentAction = nil }
            switch (component, action) {
            case ("tailscale", "install"):
                switch await tailscaleInstaller.install() {
                case .installed: componentMessage = "Tailscale을 설치했습니다. 앱을 실행해 로그인하세요."; launchTailscale()
                case .openStore: openURL(ComponentCatalog.tailscaleAppStoreURL)
                case .failed(let message): componentMessage = message
                }
            case ("tailscale", "open-store"): openURL(ComponentCatalog.tailscaleAppStoreURL)
            case ("tailscale", "launch"): launchTailscale()
            case ("tailscale", "login"):
                if let url = await tailscaleInstaller.loginURL() { NSWorkspace.shared.open(url); componentMessage = "브라우저에서 로그인을 마치면 자동으로 연결됩니다." }
                else { launchTailscale(); componentMessage = "Tailscale 앱에서 로그인하세요." }
            case ("tailscale", "connect"):
                if let failure = await tailscaleInstaller.connect() { componentMessage = failure }
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
            // Give the daemon a moment after launch/login before re-checking.
            try? await Task.sleep(for: .seconds(action == "launch" || action == "install" ? 3 : 1))
            await refreshComponents()
            await mobileRemote.retryIfNeeded()
            mobileStatus = await mobileRemote.status()
        }
    }

    private func launchTailscale() {
        let path = lastTailscaleInspection?.appPath ?? "/Applications/Tailscale.app"
        NSWorkspace.shared.openApplication(at: URL(fileURLWithPath: path), configuration: NSWorkspace.OpenConfiguration()) { _, error in
            if let error { Task { @MainActor in self.componentMessage = "Tailscale 앱을 열지 못했습니다: \(error.localizedDescription)" } }
        }
    }

    private func openURL(_ value: String) { if let url = URL(string: value) { NSWorkspace.shared.open(url) } }
}
