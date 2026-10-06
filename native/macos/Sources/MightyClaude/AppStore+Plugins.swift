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
            guard let self else { return ClaudePluginSnapshot(status: "cancelled", detail: L("plugins.windowClosed")) }
            if let reason = self.pluginWorkspaceBlockedReason(workspace) {
                return ClaudePluginSnapshot(status: "failed", detail: reason)
            }
            if self.isUpdatingCLIs { return ClaudePluginSnapshot(status: "busy", detail: L("plugins.waitForCLIUpdate")) }
            if provider == "codex" { return await self.codexPlugins.snapshot(workspace: workspace) }
            return await self.claudePlugins.snapshot(workspace: workspace)
        }, install: { [weak self] pluginID, scope in
            guard let self else { return ClaudePluginOperationResult(status: "cancelled", detail: L("settings.cliUpdate.detailClosing")) }
            return await self.performPluginMutation(workspace: workspace, provider: provider) {
                if provider == "codex" { return await self.codexPlugins.install(pluginID: pluginID, scope: scope, workspace: workspace) }
                return await self.claudePlugins.install(pluginID: pluginID, scope: scope, workspace: workspace)
            }
        }, refresh: { [weak self] name in
            guard let self else { return ClaudePluginOperationResult(status: "cancelled", detail: L("settings.cliUpdate.detailClosing")) }
            return await self.performPluginMutation(workspace: workspace, provider: provider) {
                if provider == "codex" { return await self.codexPlugins.refreshMarketplace(name: name, workspace: workspace) }
                return await self.claudePlugins.refreshMarketplace(name: name, workspace: workspace)
            }
        }, mutationBlockedReason: { [weak self] in
            guard let self else { return L("plugins.windowClosed") }
            return self.pluginMutationBlockedReason(workspace: workspace, provider: provider)
        })
    }

    /// Bind every operation to the workspace that opened the browser.
    func pluginWorkspaceBlockedReason(_ workspace: Workspace) -> String? {
        guard canManageCLIUpdates else { return L("plugins.appNotReady") }
        guard let current = snapshot.workspaces.first(where: { $0.id == workspace.id }),
              current.path == workspace.path else {
            return L("windows.updates.workspaceChanged")
        }
        return nil
    }

    func pluginMutationBlockedReason(workspace: Workspace, provider: String = "claude") -> String? {
        if let reason = pluginWorkspaceBlockedReason(workspace) { return reason }
        if isManagingPlugins { return L("plugins.operation.busy") }
        if isUpdatingCLIs { return L("plugins.changeAfterCLIUpdate") }
        if localCLIIsRunning(provider) { return L("plugins.changeAfterRun", ["provider": ProviderOptions.label(provider)]) }
        return nil
    }

    /// Reserve admission on the main actor before any suspension. The same flag
    /// prevents new Claude/Codex requests, CLI updates and host sharing from racing
    /// a plugin mutation, including operations submitted outside the sheet.
    func performPluginMutation(workspace: Workspace, provider: String = "claude",
                               operation: @MainActor () async -> ClaudePluginOperationResult) async -> ClaudePluginOperationResult {
        if let reason = pluginMutationBlockedReason(workspace: workspace, provider: provider) {
            return ClaudePluginOperationResult(status: "skipped", detail: reason)
        }
        guard !Task.isCancelled else { return ClaudePluginOperationResult(status: "cancelled", detail: L("plugins.operation.cancelled")) }
        isManagingPlugins = true
        defer { isManagingPlugins = false; resendAwaitingLoginRequests() }
        return await operation()
    }
}
