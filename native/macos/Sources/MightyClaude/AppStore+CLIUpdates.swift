import Foundation
import MightyCore

extension AppStore {
    func beginAutomaticCLIUpdatesIfNeeded(update: ((String) async -> CLIUpdateResult)? = nil) {
        guard snapshot.autoUpdateCLIs == true, !automaticCLIUpdateAttempted, canManageCLIUpdates else { return }
        automaticCLIUpdateAttempted = true
        startCLIUpdates(update: update)
    }

    /// The injectable operation is used only by the isolated diagnostic; UI
    /// actions always use the installed CLI updater.
    func startCLIUpdates(update: ((String) async -> CLIUpdateResult)? = nil) {
        guard canManageCLIUpdates, !isUpdatingCLIs, !isManagingPlugins else { return }
        isUpdatingCLIs = true
        cliUpdateFinishedAt = nil
        cliUpdateResults.removeAll()
        cliUpdateTask = Task { [weak self] in
            guard let self else { return }
            defer {
                self.updatingCLI = nil
                self.isUpdatingCLIs = false
                self.cliUpdateFinishedAt = Date()
                self.cliUpdateTask = nil
            }
            for provider in ProviderOptions.ids {
                guard !Task.isCancelled, self.canManageCLIUpdates else { break }
                if self.remoteState.host.enabled || self.remoteBusy || self.localCLIIsRunning(provider) {
                    self.cliUpdateResults[provider] = CLIUpdateResult(provider: provider, status: "skipped", beforeVersion: nil, afterVersion: nil, method: "unknown", detail: self.remoteState.host.enabled || self.remoteBusy ? "원격 공유·연결 작업이 끝난 후 다시 업데이트하세요." : "이 CLI로 작업 중입니다. 작업 완료 후 다시 업데이트하세요.", output: "")
                    continue
                }
                self.updatingCLI = provider
                let result: CLIUpdateResult
                if let update { result = await update(provider) }
                else { result = await self.cliUpdater.update(provider: provider) }
                self.cliUpdateResults[provider] = result
                self.updatingCLI = nil
            }
            if !Task.isCancelled, self.canManageCLIUpdates { await self.refreshRuntimeAfterCLIUpdate() }
        }
    }
}
