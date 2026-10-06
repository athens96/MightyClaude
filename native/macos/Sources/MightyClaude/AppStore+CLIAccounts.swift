import Foundation
import MightyCore

/// Signing the three CLIs in and out. Status comes from the CLIs themselves
/// (or Gemini's account files); sign-in is interactive, so it runs in a
/// terminal pane of a local workspace with the command run for the user.
/// Settings is a sheet, so problems are reported per provider in its row.
extension AppStore {
    static let cliLoginPollInterval: TimeInterval = 5
    static let cliLoginPollLimit: TimeInterval = 10 * 60

    func refreshCLIAccounts(_ providers: [String] = ProviderOptions.ids) {
        // The help capture shows the demo profile's accounts; no CLI is asked.
        guard !helpCapturing else { return }
        for provider in providers where cliAccountRefreshing.insert(provider).inserted {
            invalidateLocalModels(provider: provider)
            let service = cliAccountService
            Task { [weak self] in
                let status = await service.status(provider: provider)
                await MainActor.run {
                    guard let self else { return }
                    self.cliAccountRefreshing.remove(provider)
                    guard !self.cliAccountBusy.contains(provider) else { return } // an action owns the row now
                    self.cliAccounts[provider] = status
                    if status.loggedIn == true, status.accessVerified != false {
                        // A sign-in terminal still pending is confirmed by this read.
                        if self.cliLoginPending.contains(provider) { self.autoLoginGate.succeeded(provider: provider) }
                        self.endCLILogin(provider)
                    }
                }
            }
        }
    }

    /// Why the account cannot change right now, if anything is using it.
    func cliAccountBlockedReason(_ provider: String) -> String? {
        if snapshot.sessions.contains(where: { $0.kind == "claude" && $0.provider == provider && ($0.status == "running" || pendingRuns.contains($0.id)) }) {
            return L("settings.cliAccounts.blockedRunning", ["provider": ProviderOptions.label(provider)])
        }
        // A sign-in terminal opened from here replaces itself.
        return accountBusyReason(provider, ignoringTerminalLogin: true)
    }

    func logoutCLI(_ provider: String, thenLogin option: CLILoginOption? = nil) {
        cliAccountMessages[provider] = nil
        if let reason = cliAccountBlockedReason(provider) { cliAccountMessages[provider] = reason; return }
        guard cliAccounts[provider]?.canSignOut != false else { cliAccountMessages[provider] = cliAccounts[provider]?.detail ?? L("settings.cliAccounts.cannotSignOut"); return }
        guard cliAccountBusy.insert(provider).inserted else { cliAccountMessages[provider] = L("settings.cliAccounts.busy"); return }
        endCLILogin(provider)
        let service = cliAccountService
        Task { [weak self] in
            let status = await service.logout(provider: provider)
            await MainActor.run {
                guard let self else { return }
                self.cliAccounts[provider] = status
                self.cliAccountBusy.remove(provider)
                self.invalidateLocalModels(provider: provider)
                if status.loggedIn == true { self.cliAccountMessages[provider] = L("settings.cliAccounts.signOutUnconfirmed") }
                else if let option { self.startCLILogin(provider, option: option) }
            }
        }
    }

    /// Opens a terminal pane in a local workspace and runs the sign-in
    /// command there. The CLI opens the browser; the pane shows what it asks.
    func startCLILogin(_ provider: String, option: CLILoginOption = .account) {
        cliAccountMessages[provider] = nil
        if let reason = cliAccountBlockedReason(provider) { cliAccountMessages[provider] = reason; return }
        guard !cliAccountBusy.contains(provider) else { cliAccountMessages[provider] = L("settings.cliAccounts.busy"); return }
        guard let command = CLIAccountSupport.loginCommand(provider: provider, option: option) else { return }
        let workspace = activeWorkspace ?? snapshot.workspaces.first
        guard let workspace else { cliAccountMessages[provider] = L("settings.cliAccounts.noWorkspace"); return }
        guard snapshot.sessions.count < 128 else { cliAccountMessages[provider] = L("settings.cliAccounts.tooManyPanes"); return }
        // addSession refuses while a sheet is up, so Settings closes first and
        // comes back if the pane could not be added.
        let settingsWasOpen = showSettings
        showSettings = false
        selectWorkspace(workspace.id)
        guard let id = addSession(kind: "shell", workspaceId: workspace.id) else {
            showSettings = settingsWasOpen
            cliAccountMessages[provider] = L("settings.cliAccounts.terminalFailed")
            return
        }
        let settingUpBedrock = provider == "claude" && option == .bedrock
        updateSession(id) { $0.title = settingUpBedrock ? L("settings.cliAccounts.bedrockTerminalTitle") : L("loginRecovery.terminalTitle", ["provider": ProviderOptions.label(provider)]) }
        // The app wrote this command itself, so it is the one thing that still
        // presses Enter for the user (§1.5).
        pendingTerminalInput[id] = TerminalInput(text: command, autoRun: true)
        endCLILogin(provider)
        if settingUpBedrock {
            // Bedrock auth status reports backend configuration, even before
            // AWS credentials work. It cannot complete an OAuth polling flow.
            return
        }
        cliLoginPending.insert(provider); cliLoginSessions[provider] = id
        // This poll cannot see the terminal's command end, so only a status
        // that turns from signed out to signed in may resend waiting requests.
        let startedSignedOut = cliAccounts[provider]?.loggedIn == false
        // The poll lives here, not in the Settings view, which is closed now.
        let service = cliAccountService
        cliLoginTasks[provider] = Task { [weak self] in
            let started = Date()
            while !Task.isCancelled, Date().timeIntervalSince(started) < Self.cliLoginPollLimit {
                try? await Task.sleep(for: .seconds(Self.cliLoginPollInterval))
                guard !Task.isCancelled else { return }
                let status = await service.status(provider: provider)
                let finished = await MainActor.run { () -> Bool in
                    guard let self, self.cliLoginSessions[provider] == id else { return true }
                    self.cliAccounts[provider] = status
                    if status.loggedIn == true, status.accessVerified != false {
                        self.autoLoginGate.succeeded(provider: provider)
                        self.invalidateLocalModels(provider: provider)
                        self.endCLILogin(provider)
                        // Panes whose run lost this sign-in get their request back.
                        if startedSignedOut, self.loginRequired.values.contains(provider) || !self.loginRetries.sessions(provider: provider).isEmpty {
                            Task { await self.loginRestored(provider, status: status) }
                        }
                        return true
                    }
                    return false
                }
                if finished { return }
            }
            await MainActor.run { if self?.cliLoginSessions[provider] == id { self?.endCLILogin(provider) } }
        }
    }

    /// Stops waiting for a sign-in (finished, abandoned, or its pane closed).
    func endCLILogin(_ provider: String) {
        cliLoginTasks.removeValue(forKey: provider)?.cancel()
        cliLoginSessions.removeValue(forKey: provider)
        cliLoginPending.remove(provider)
    }
    func cliLoginEnded(sessionID: String) {
        for (provider, id) in cliLoginSessions where id == sessionID {
            endCLILogin(provider)
            refreshCLIAccounts([provider])
        }
    }
}
