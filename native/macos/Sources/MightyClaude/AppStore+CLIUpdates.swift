import Foundation
import MightyCore

/// Background updates: the CLIs (the existing automatic update setting) and
/// the Claude user-scope plugins and Codex marketplaces (their own setting, on
/// by default). A pass runs at launch and every six hours; a provider is only
/// touched once it has been idle for three minutes, and one that was not is
/// retried when it is. Sends to a provider being updated in the background
/// wait in the pane's queue and run when its step ends. Every request starts a
/// new CLI process, so an update applies from the next request.
extension AppStore {
    var automaticCLIUpdatesOn: Bool { snapshot.autoUpdateCLIs == true }
    var automaticPluginUpdatesOn: Bool { snapshot.autoUpdatePlugins != false }

    func beginAutomaticCLIUpdatesIfNeeded(update: ((String) async -> CLIUpdateResult)? = nil) {
        // Diagnostics inject a fake update: they never start the periodic
        // updater and never reach a real plugin command.
        if update == nil { startAutomaticUpdateLoop() }
        let plugins = update == nil && automaticPluginUpdatesOn
        guard automaticCLIUpdatesOn || plugins, !automaticCLIUpdateAttempted, canManageCLIUpdates else { return }
        automaticCLIUpdateAttempted = true
        cliAutoUpdateSchedule.passStarted(at: Date())
        startCLIUpdates(update: update, automatic: true)
    }

    private func startAutomaticUpdateLoop() {
        guard cliAutoUpdateLoop == nil else { return }
        cliAutoUpdateLoop = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(CLIAutoUpdateSchedule.tick))
                guard !Task.isCancelled, let self, !self.ending else { return }
                self.runAutomaticUpdatesIfDue()
            }
        }
    }

    private func runAutomaticUpdatesIfDue(now: Date = Date()) {
        guard canManageCLIUpdates, !isUpdatingCLIs, !isManagingPlugins else { return }
        let enabled = automaticCLIUpdatesOn || automaticPluginUpdatesOn
        if cliAutoUpdateSchedule.isDue(at: now, enabled: enabled) {
            automaticCLIUpdateAttempted = true
            cliAutoUpdateSchedule.passStarted(at: now)
            startCLIUpdates(automatic: true)
            return
        }
        let due = cliAutoUpdateSchedule.dueRetries(enabled: enabled) { readyForBackgroundUpdate($0, now: now) }
        if !due.isEmpty { startCLIUpdates(providers: due, automatic: true) }
    }

    /// Idle long enough for a background update, and nobody is changing its sign-in.
    func readyForBackgroundUpdate(_ provider: String, now: Date = Date()) -> Bool {
        // A queue paused by a failed run waits on the user, not on this CLI; it
        // must not hold the provider's updates back indefinitely.
        let queued = snapshot.sessions.contains { $0.provider == provider && $0.kind != "shell" && $0.status != "error" && !(queuedInputs[$0.id] ?? []).isEmpty }
        return !accountChangeInProgress(provider)
            && CLIAutoUpdateSchedule.idleLongEnough(busy: localCLIIsRunning(provider), queued: queued, lastActive: providerLastActive[provider], now: now)
    }

    /// A background step works on this pane's provider: its sends queue.
    func backgroundUpdateHolds(_ session: RunSession) -> Bool {
        session.kind != "shell" && automaticUpdateRunning && (updatingCLI == session.provider || updatingPluginsFor == session.provider)
    }

    /// Runs the queues a background step held back, once it ended.
    private func releaseHeldQueues(_ provider: String) {
        for id in heldForUpdate.sorted() {
            guard let session = snapshot.sessions.first(where: { $0.id == id }) else { heldForUpdate.remove(id); continue }
            guard session.provider == provider, !backgroundUpdateHolds(session) else { continue }
            heldForUpdate.remove(id)
            runNextQueuedInput(id)
        }
    }

    /// The injectable operation is used only by the isolated diagnostic; UI
    /// actions always use the installed CLI updater. Plugins update only in
    /// automatic passes of the real updater.
    func startCLIUpdates(update: ((String) async -> CLIUpdateResult)? = nil, providers: [String] = ProviderOptions.ids, automatic: Bool = false) {
        guard canManageCLIUpdates, !isUpdatingCLIs, !isManagingPlugins else { return }
        isUpdatingCLIs = true
        automaticUpdateRunning = automatic
        cliUpdateFinishedAt = nil
        if providers == ProviderOptions.ids { cliUpdateResults.removeAll() }
        let defers = automatic && update == nil
        let updatesCLIs = !defers || automaticCLIUpdatesOn
        let updatesPlugins = defers && automaticPluginUpdatesOn
        cliUpdateTask = Task { [weak self] in
            guard let self else { return }
            defer {
                self.updatingCLI = nil
                self.updatingPluginsFor = nil
                self.isUpdatingCLIs = false
                self.automaticUpdateRunning = false
                self.cliUpdateFinishedAt = Date()
                self.cliUpdateTask = nil
                for provider in providers { self.releaseHeldQueues(provider) }
                self.resendAwaitingLoginRequests()
            }
            for provider in providers where updatesCLIs {
                guard !Task.isCancelled, self.canManageCLIUpdates else { break }
                // A sign-in or sign-out of this provider (any of them) makes it busy too.
                let changingAccount = self.accountChangeInProgress(provider)
                let busy = defers ? !self.readyForBackgroundUpdate(provider) : self.localCLIIsRunning(provider) || changingAccount
                if busy {
                    if defers { self.cliAutoUpdateSchedule.skippedBusy(provider) }
                    let detail = defers ? L("settings.cliUpdate.detailDeferred") : changingAccount ? L("loginRecovery.busy") : L("settings.cliUpdate.detailInUse")
                    self.cliUpdateResults[provider] = CLIUpdateResult(provider: provider, status: "skipped", beforeVersion: nil, afterVersion: nil, method: "unknown", detail: detail, output: "")
                    continue
                }
                self.updatingCLI = provider
                let result: CLIUpdateResult
                if let update { result = await update(provider) }
                else { result = await self.cliUpdater.update(provider: provider) }
                self.cliUpdateResults[provider] = result
                self.updatingCLI = nil
                if defers { self.cliAutoUpdateSchedule.updated(provider) }
                self.releaseHeldQueues(provider)
            }
            if updatesPlugins { await self.updatePluginsInBackground(providers) }
            if !Task.isCancelled, self.canManageCLIUpdates { await self.refreshRuntimeAfterCLIUpdate() }
        }
    }

    /// Claude: `claude plugin update --scope user --json` per user-scope plugin,
    /// never accepting a changed marketplace command. Codex: `codex plugin
    /// marketplace upgrade`. Only for an idle provider while the plugin browser
    /// is idle, and no new provider starts once the plugin budget is spent.
    private func updatePluginsInBackground(_ providers: [String]) async {
        let started = Date()
        // User-scope plugins apply everywhere; the home folder ties the
        // update to no project.
        let home = Workspace(name: "Home", path: FileManager.default.homeDirectoryForCurrentUser.path)
        for provider in providers where ["claude", "codex"].contains(provider) {
            guard !Task.isCancelled, canManageCLIUpdates else { return }
            let remaining = CLIAutoUpdateSchedule.pluginBudget - Date().timeIntervalSince(started)
            let busy = !readyForBackgroundUpdate(provider)
            guard remaining > 0, CLIAutoUpdateSchedule.mayUpdatePlugins(busy: busy, managingPlugins: isManagingPlugins) else {
                if busy || remaining <= 0 { cliAutoUpdateSchedule.skippedBusy(provider) }
                pluginUpdateResults[provider] = PluginAutoUpdateResult(status: "skipped", detail: busy || remaining <= 0 ? L("settings.cliUpdate.detailDeferred") : L("pluginAutoUpdate.busy"))
                continue
            }
            updatingPluginsFor = provider
            let result = provider == "codex"
                ? await codexPlugins.upgradeMarketplaces(workspace: home)
                : await claudePlugins.updateInstalled(workspace: home, budget: remaining)
            updatingPluginsFor = nil
            pluginUpdateResults[provider] = result
            // Plugin commands are scanned again for the next completion list.
            slashCatalogs = slashCatalogs.filter { !$0.key.hasPrefix(provider + "|") }
            releaseHeldQueues(provider)
        }
    }
}
