import Foundation
import MightyCore

extension AppStore {
    /// A deliberate reset covers local Claude panes only. Running requests keep
    /// their configuration, so reset waits until all affected panes are idle.
    func resetClaudeModels() {
        guard !ending, !claudeModelResetInProgress else { return }
        let sessions = snapshot.sessions.filter { $0.provider == "claude" && localModelContext(for: $0) != nil }
        guard !sessions.contains(where: { $0.status == "running" || pendingRuns.contains($0.id) }) else {
            cliAccountMessages["claude"] = L("settings.cliAccounts.modelReset.running")
            return
        }
        claudeModelResetInProgress = true
        cliAccountMessages["claude"] = L("settings.cliAccounts.modelReset.reloading")
        for session in sessions {
            updateSession(session.id) { $0.model = "default"; $0.settings.effort = "default" }
            modelPriorCatalogs.removeValue(forKey: session.id)
        }
        modelRefreshSelections = modelRefreshSelections.filter { $0.key.provider != "claude" }
        localModels.discard(provider: "claude")
        modelRefreshRevision &+= 1
        let contexts = snapshot.workspaces.map {
            LocalModelContext(workspaceID: $0.id, path: $0.path, provider: "claude")
        }
        Task { [weak self] in
            guard let self else { return }
            await providers.discardModelCatalogs(provider: "claude")
            guard !ending else { claudeModelResetInProgress = false; return }
            // Start together; each workspace can have different CLI settings.
            let tasks = contexts.map { self.localModels.request($0, force: true, invalidate: true) }
            var failed = 0
            for task in tasks {
                let result = await task.value
                if result?.modelCatalog.source != "cli" { failed += 1 }
            }
            claudeModelResetInProgress = false
            modelRefreshRevision &+= 1
            guard !ending else { return }
            resendAwaitingLoginRequests()
            if contexts.isEmpty {
                cliAccountMessages["claude"] = L("settings.cliAccounts.modelReset.cacheCleared")
            } else if failed > 0 {
                cliAccountMessages["claude"] = L("settings.cliAccounts.modelReset.partial", ["count": "\(failed)"])
            } else {
                cliAccountMessages["claude"] = L("settings.cliAccounts.modelReset.done", ["count": "\(contexts.count)"])
            }
        }
    }
}
