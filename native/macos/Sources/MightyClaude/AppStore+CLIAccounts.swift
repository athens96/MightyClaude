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
        for provider in providers where cliAccountRefreshing.insert(provider).inserted {
            let service = cliAccountService
            Task { [weak self] in
                let status = await service.status(provider: provider)
                await MainActor.run {
                    guard let self else { return }
                    self.cliAccountRefreshing.remove(provider)
                    guard !self.cliAccountBusy.contains(provider) else { return } // an action owns the row now
                    self.cliAccounts[provider] = status
                    if status.loggedIn == true { self.endCLILogin(provider) }
                }
            }
        }
    }

    /// Why the account cannot change right now, if anything is using it.
    func cliAccountBlockedReason(_ provider: String) -> String? {
        if snapshot.sessions.contains(where: { $0.kind == "claude" && $0.provider == provider && $0.status == "running" }) {
            return "\(ProviderOptions.label(provider)) 실행이 진행 중입니다. 끝난 뒤에 계정을 바꾸세요."
        }
        if isUpdatingCLIs { return "CLI 업데이트가 끝난 뒤에 다시 시도하세요." }
        return nil
    }

    func logoutCLI(_ provider: String, thenLogin option: CLILoginOption? = nil) {
        cliAccountMessages[provider] = nil
        if let reason = cliAccountBlockedReason(provider) { cliAccountMessages[provider] = reason; return }
        guard cliAccounts[provider]?.canSignOut != false else { cliAccountMessages[provider] = cliAccounts[provider]?.detail ?? "이 로그인 방식은 앱에서 로그아웃할 수 없습니다."; return }
        guard cliAccountBusy.insert(provider).inserted else { cliAccountMessages[provider] = "이미 처리 중입니다. 잠시 후 다시 시도하세요."; return }
        endCLILogin(provider)
        let service = cliAccountService
        Task { [weak self] in
            let status = await service.logout(provider: provider)
            await MainActor.run {
                guard let self else { return }
                self.cliAccounts[provider] = status
                self.cliAccountBusy.remove(provider)
                if status.loggedIn == true { self.cliAccountMessages[provider] = "로그아웃을 확인하지 못했습니다. 터미널에서 직접 로그아웃해 보세요." }
                else if let option { self.startCLILogin(provider, option: option) }
            }
        }
    }

    /// Opens a terminal pane in a local workspace and runs the sign-in
    /// command there. The CLI opens the browser; the pane shows what it asks.
    func startCLILogin(_ provider: String, option: CLILoginOption = .account) {
        cliAccountMessages[provider] = nil
        if let reason = cliAccountBlockedReason(provider) { cliAccountMessages[provider] = reason; return }
        guard !cliAccountBusy.contains(provider) else { cliAccountMessages[provider] = "이미 처리 중입니다. 잠시 후 다시 시도하세요."; return }
        guard let command = CLIAccountSupport.loginCommand(provider: provider, option: option) else { return }
        let workspace = (activeWorkspace?.remote == nil ? activeWorkspace : nil) ?? snapshot.workspaces.first { $0.remote == nil }
        guard let workspace else { cliAccountMessages[provider] = "로그인 터미널을 열 로컬 워크스페이스가 없습니다. 프로젝트 폴더를 먼저 여세요."; return }
        guard snapshot.sessions.count < 128 else { cliAccountMessages[provider] = "실행 창이 너무 많아 로그인 터미널을 열 수 없습니다."; return }
        // addSession refuses while a sheet is up, so Settings closes first and
        // comes back if the pane could not be added.
        let settingsWasOpen = showSettings
        showSettings = false
        selectWorkspace(workspace.id)
        guard let id = addSession(kind: "shell", workspaceId: workspace.id) else {
            showSettings = settingsWasOpen
            cliAccountMessages[provider] = "로그인 터미널을 열지 못했습니다."
            return
        }
        updateSession(id) { $0.title = "\(ProviderOptions.label(provider)) 로그인" }
        // The app wrote this command itself, so it is the one thing that still
        // presses Enter for the user (§1.5).
        pendingTerminalInput[id] = TerminalInput(text: command, autoRun: true)
        endCLILogin(provider)
        cliLoginPending.insert(provider); cliLoginSessions[provider] = id
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
                    if status.loggedIn == true { self.endCLILogin(provider); return true }
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
