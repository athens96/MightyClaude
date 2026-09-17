import Foundation
import MightyCore

extension AppStore {
    func openPluginBrowser(sessionID: String) {
        guard !hasModal,
              let session = snapshot.sessions.first(where: { $0.id == sessionID }),
              session.kind == "claude", ["claude", "codex"].contains(session.provider),
              let workspace = snapshot.workspaces.first(where: { $0.id == session.workspaceId }) else { return }
        let provider = session.provider
        pluginBrowser = ClaudePluginBrowserModel(workspace: workspace, provider: provider, load: { [weak self] in
            guard let self else { return ClaudePluginSnapshot(status: "cancelled", detail: "플러그인 창이 닫혔습니다.") }
            if let reason = self.pluginWorkspaceBlockedReason(workspace) {
                return ClaudePluginSnapshot(status: workspace.remote == nil ? "failed" : "remote", detail: reason)
            }
            if self.isUpdatingCLIs { return ClaudePluginSnapshot(status: "busy", detail: "CLI 업데이트가 끝난 후 다시 확인하세요.") }
            if provider == "codex" { return await self.codexPlugins.snapshot(workspace: workspace) }
            return await self.claudePlugins.snapshot(workspace: workspace)
        }, install: { [weak self] pluginID, scope in
            guard let self else { return ClaudePluginOperationResult(status: "cancelled", detail: "앱이 종료 중입니다.") }
            return await self.performPluginMutation(workspace: workspace, provider: provider) {
                if provider == "codex" { return await self.codexPlugins.install(pluginID: pluginID, scope: scope, workspace: workspace) }
                return await self.claudePlugins.install(pluginID: pluginID, scope: scope, workspace: workspace)
            }
        }, refresh: { [weak self] name in
            guard let self else { return ClaudePluginOperationResult(status: "cancelled", detail: "앱이 종료 중입니다.") }
            return await self.performPluginMutation(workspace: workspace, provider: provider) {
                if provider == "codex" { return await self.codexPlugins.refreshMarketplace(name: name, workspace: workspace) }
                return await self.claudePlugins.refreshMarketplace(name: name, workspace: workspace)
            }
        }, mutationBlockedReason: { [weak self] in
            guard let self else { return "플러그인 창이 닫혔습니다." }
            return self.pluginMutationBlockedReason(workspace: workspace, provider: provider)
        })
    }

    /// Bind every operation to the workspace that opened the browser. A remote
    /// path must never become the working directory of a local CLI command.
    func pluginWorkspaceBlockedReason(_ workspace: Workspace) -> String? {
        guard canManageCLIUpdates else { return "앱이 준비되지 않았거나 종료 중입니다." }
        guard let current = snapshot.workspaces.first(where: { $0.id == workspace.id }),
              current.path == workspace.path, current.remote == workspace.remote else {
            return "워크스페이스가 변경되었습니다. 플러그인 창을 다시 여세요."
        }
        guard current.remote == nil else { return "원격 워크스페이스의 플러그인은 해당 컴퓨터의 MightyClaude에서 관리하세요." }
        return nil
    }

    func pluginMutationBlockedReason(workspace: Workspace, provider: String = "claude") -> String? {
        if let reason = pluginWorkspaceBlockedReason(workspace) { return reason }
        if isManagingPlugins { return "다른 플러그인 작업이 진행 중입니다." }
        if isUpdatingCLIs { return "CLI 업데이트가 끝난 후 플러그인을 변경하세요." }
        if localCLIIsRunning(provider) { return "실행 중인 \(ProviderOptions.label(provider)) 작업이 끝난 후 플러그인을 변경하세요." }
        if remoteBusy || remoteState.host.enabled { return "원격 공유·연결 작업을 마친 후 플러그인을 변경하세요." }
        return nil
    }

    /// Reserve admission on the main actor before any suspension. The same flag
    /// prevents new Claude/Codex requests, CLI updates and host sharing from racing
    /// a plugin mutation, including operations submitted outside the sheet.
    func performPluginMutation(workspace: Workspace, provider: String = "claude",
                               operation: @MainActor () async -> ClaudePluginOperationResult) async -> ClaudePluginOperationResult {
        if let reason = pluginMutationBlockedReason(workspace: workspace, provider: provider) {
            return ClaudePluginOperationResult(status: workspace.remote == nil ? "skipped" : "remote", detail: reason)
        }
        guard !Task.isCancelled else { return ClaudePluginOperationResult(status: "cancelled", detail: "플러그인 작업을 취소했습니다.") }
        isManagingPlugins = true
        defer { isManagingPlugins = false }
        return await operation()
    }
}
