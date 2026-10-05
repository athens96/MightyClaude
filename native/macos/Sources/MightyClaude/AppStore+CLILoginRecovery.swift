import Foundation
import MightyCore

/// What a pane's "sign-in lost" card shows about its provider's one
/// background sign-in. No state means the card still offers the button.
struct BackgroundLoginState: Equatable {
    enum Phase: Equatable { case starting, waiting, failed(String) }
    let id: UUID
    var phase: Phase
    var url: URL?
    var asksForCode = false
    /// Started by the app on a lost sign-in, not by the card's button.
    var automatic = false
    /// Gemini signs in in this visible terminal pane rather than in the background.
    var terminalSessionId: String?
}

/// The running sign-in behind a `BackgroundLoginState` with the same id.
final class BackgroundLoginJob {
    let id: UUID
    var task: Task<Void, Never>?
    var login: CLIBackgroundLogin?
    init(id: UUID) { self.id = id }
}

/// A run that ends because the CLI lost its sign-in raises a card in its
/// pane and, with the Settings switch on (the default), starts the sign-in at
/// once; otherwise one press does. It signs in again in the background (the CLI
/// opens the browser itself) and, once signed in, resends the failed request in
/// every pane of that provider still waiting for it. Every request starts a new CLI
/// process that resumes the conversation, so the new sign-in applies at once.
extension AppStore {
    /// Someone is changing this provider's sign-in right now: Settings, a
    /// sign-in terminal, or a background sign-in. The CLI updater treats the
    /// provider as busy while this holds.
    func accountChangeInProgress(_ provider: String, ignoringTerminalLogin: Bool = false) -> Bool {
        cliAccountBusy.contains(provider) || backgroundLoginJobs[provider] != nil
            || (!ignoringTerminalLogin && cliLoginPending.contains(provider))
    }

    /// The one answer to "can this provider's sign-in change now", shared by the
    /// background sign-in and Settings' sign-in and sign-out. A terminal
    /// sign-in replaces itself, so Settings may ignore a pending one.
    func accountBusyReason(_ provider: String, ignoringTerminalLogin: Bool = false) -> String? {
        if accountChangeInProgress(provider, ignoringTerminalLogin: ignoringTerminalLogin) { return L("loginRecovery.busy") }
        if isUpdatingCLIs { return L("loginRecovery.updating") }
        return nil
    }

    /// Every request goes through `start`. A new one is the pane's latest, so
    /// it replaces any retry and closes the card that promised one.
    func requestSent(_ id: String, session: RunSession, input: String, attachments: [RunAttachment]) {
        let generation = sendGenerations[id, default: 0] &+ 1
        sendGenerations[id] = generation
        providerLastActive[session.provider] = Date()
        dismissLoginRequired(id)
        if session.kind == "claude", CLIAuthFailure.providers.contains(session.provider) {
            inFlightRequests[id] = (CLILoginRetryRequest(sessionId: id, provider: session.provider, input: input, attachments: attachments, sentAt: Date()), generation)
        } else { inFlightRequests.removeValue(forKey: id) }
    }

    /// The runner puts the reason on the run's final status: only its last
    /// failure counts.
    func receiveLoginSignal(_ event: RunEvent) {
        guard event.type == "status", let status = event.status, status != "running" else { return }
        let request = inFlightRequests.removeValue(forKey: event.sessionId)
        if status == "completed" { dismissLoginRequired(event.sessionId) }
        guard status == "error", event.reason == "auth", let request else { return }
        confirmLoginLost(request.request, generation: request.generation)
    }

    /// A dropped sign-in usually leaves credentials on disk, so the status may
    /// still say signed in; it only rules out methods a sign-in cannot renew.
    private func confirmLoginLost(_ request: CLILoginRetryRequest, generation: UInt64) {
        let service = cliAccountService
        Task { [weak self] in
            let status = await service.status(provider: request.provider)
            guard let self, !self.ending else { return }
            if !self.cliAccountBusy.contains(request.provider) { self.cliAccounts[request.provider] = status }
            // Anything sent in the pane since makes this check stale.
            guard self.sendGenerations[request.sessionId] == generation, CLIAuthFailure.signInCanFix(status),
                  !self.closingSessions.contains(request.sessionId),
                  let session = self.snapshot.sessions.first(where: { $0.id == request.sessionId }), session.provider == request.provider,
                  session.status != "running", !self.pendingRuns.contains(session.id) else { return }
            self.loginRequired[session.id] = request.provider
            self.loginRetries.remember(request)
            self.startAutomaticLoginIfAllowed(request, status: status)
        }
    }

    /// The automatic start: one sign-in per provider, however many panes lost
    /// it together, and none for a while after one ended. A request that was
    /// itself resent, or sent before the last sign-in, only raises the card.
    func startAutomaticLoginIfAllowed(_ request: CLILoginRetryRequest, status: CLIAccountStatus) {
        let provider = request.provider
        let active = backgroundLoginJobs[provider] != nil || accountBusyReason(provider) != nil
        guard !ending, autoLoginGate.shouldStart(provider: provider, enabled: snapshot.autoLoginCLIs != false, status: status, loginActive: active,
                                                 resent: request.resent, sentAt: request.sentAt) else { return }
        startBackgroundLogin(provider, automatic: true, sessionId: request.sessionId)
    }

    func forgetLoginRecovery(_ id: String) {
        inFlightRequests.removeValue(forKey: id)
        sendGenerations.removeValue(forKey: id)
        dismissLoginRequired(id)
    }

    /// Closing the last card of a provider also ends its background sign-in,
    /// which would otherwise wait with nothing left to show it.
    func dismissLoginRequired(_ id: String) {
        loginRetries.drop(sessionId: id)
        awaitingLoginResend.remove(id)
        loginCardNotes.removeValue(forKey: id)
        guard let provider = loginRequired.removeValue(forKey: id) else { return }
        if !loginRequired.values.contains(provider) { cancelBackgroundLogin(provider) }
    }

    /// `sessionId`: the pane whose run lost the sign-in; Gemini's terminal opens beside it.
    func startBackgroundLogin(_ provider: String, automatic: Bool = false, sessionId: String? = nil) {
        guard !ending, CLIAuthFailure.providers.contains(provider), backgroundLoginJobs[provider] == nil,
              let command = CLIAccountSupport.loginCommand(provider: provider, option: .account) else { return }
        let id = UUID()
        if let reason = accountBusyReason(provider) {
            backgroundLogins[provider] = BackgroundLoginState(id: id, phase: .failed(reason)); return
        }
        cliAccountMessages[provider] = nil
        if provider == "gemini" { startGeminiTerminalLogin(id: id, command: command, automatic: automatic, sessionId: sessionId); return }
        backgroundLogins[provider] = BackgroundLoginState(id: id, phase: .starting, automatic: automatic)
        let job = BackgroundLoginJob(id: id)
        backgroundLoginJobs[provider] = job
        let service = cliAccountService
        job.task = Task { [weak self] in
            let environment = await service.commandEnvironment()
            guard let self, !Task.isCancelled, self.backgroundLoginJobs[provider]?.id == id else { return }
            let login = CLIBackgroundLogin(command: command, environment: environment, directory: FileManager.default.homeDirectoryForCurrentUser) { [weak self] output in
                Task { @MainActor in
                    guard let self, self.backgroundLogins[provider]?.id == id else { return }
                    self.backgroundLogins[provider]?.url = output.url
                    self.backgroundLogins[provider]?.asksForCode = output.asksForCode
                }
            }
            job.login = login
            do { try await login.start() } catch {
                self.failBackgroundLogin(provider, id: id, message: L("loginRecovery.startFailed")); return
            }
            guard !Task.isCancelled, self.backgroundLoginJobs[provider]?.id == id else { login.cancel(); return }
            self.backgroundLogins[provider]?.phase = .waiting
            let outcome = await CLILoginWait.run(isRunning: {
                let running = login.isRunning
                // A link printed as the very last output is complete once the command ended.
                if !running { login.finishOutput() }
                return running
            }, exitCode: { login.exitCode }, status: { await service.status(provider: provider) },
               interval: Self.cliLoginPollInterval, limit: Self.cliLoginPollLimit)
            if case .loggedIn = outcome {
                // Signed in while the command still runs: let it finish on its own
                // (it may still be saving), and only then stop a lingering one.
                for _ in 0..<60 where login.isRunning { try? await Task.sleep(for: .milliseconds(250)) }
            }
            login.cancel()
            guard self.backgroundLoginJobs[provider]?.id == id else { return }
            switch outcome {
            case .loggedIn(let status):
                // Held before the job goes, so a run failing while the app catches up never starts another.
                self.autoLoginGate.succeeded(provider: provider)
                self.backgroundLoginJobs.removeValue(forKey: provider)
                await self.loginRestored(provider, status: status)
            case .exited(let status):
                self.cliAccounts[provider] = status
                self.failBackgroundLogin(provider, id: id, message: L("loginRecovery.exited"))
            case .timedOut(let status):
                self.cliAccounts[provider] = status
                self.failBackgroundLogin(provider, id: id, message: L("loginRecovery.timedOut"))
            case .cancelled: break
            }
        }
    }

    private func failBackgroundLogin(_ provider: String, id: UUID, message: String) {
        guard backgroundLoginJobs[provider]?.id == id else { return }
        backgroundLoginJobs.removeValue(forKey: provider)
        autoLoginGate.stopped(provider: provider)
        backgroundLogins[provider] = BackgroundLoginState(id: id, phase: .failed(message))
    }

    /// Writes a code the CLI asked to paste. It goes to the process only.
    func sendBackgroundLoginCode(_ provider: String, code: String) {
        backgroundLoginJobs[provider]?.login?.send(code: code)
    }

    func cancelBackgroundLogin(_ provider: String) {
        if let job = backgroundLoginJobs.removeValue(forKey: provider) {
            job.task?.cancel(); job.login?.cancel()
            autoLoginGate.stopped(provider: provider)
        }
        backgroundLogins.removeValue(forKey: provider)
    }

    /// Gemini has no sign-in command: its sign-in is the interactive CLI
    /// ("Login with Google"), which cannot run hidden, so it runs in a terminal
    /// pane beside the failed pane (to its right, or a tab in its group when no
    /// split fits). It counts as signed in once `oauth_creds.json` appears or is
    /// rewritten after the start (`CLIGeminiLogin`), and then resends like a
    /// background sign-in.
    /// - An automatic start never moves the user: the pane is added without
    ///   being selected, switching workspace or changing the layout mode. With a
    ///   sheet open no pane can be added, so the card just keeps its button and
    ///   nothing is held.
    /// - The pane of an earlier sign-in, while still open, is used again (and
    ///   selected for the card's button) instead of stacking another. Nothing is
    ///   typed into it, since Gemini may still be running there.
    /// - The pane stays open after signing in, as a Settings sign-in terminal
    ///   does: it now holds a live Gemini session the user may be typing in, and
    ///   closing it would end that session. Closing it before signing in ends the wait.
    private func startGeminiTerminalLogin(id: UUID, command: String, automatic: Bool, sessionId: String?) {
        let provider = "gemini"
        let failed = sessionId.flatMap { id in snapshot.sessions.first { $0.id == id } }
        let reused = geminiLoginTerminal.flatMap { terminalPaneOpen($0) ? $0 : nil }
        if reused == nil, automatic, hasModal { return }
        let job = BackgroundLoginJob(id: id)
        backgroundLoginJobs[provider] = job
        backgroundLogins[provider] = BackgroundLoginState(id: id, phase: .starting, automatic: automatic)
        var pane = reused
        if let reused { if !automatic { selectSession(reused) } }
        else if automatic { pane = failed.flatMap { insertLoginTerminal(beside: $0) } }
        else if let workspaceId = failed?.workspaceId ?? activeWorkspace?.id ?? snapshot.workspaces.first?.id {
            let group = failed.flatMap { session in layoutForWorkspace(session.workspaceId)?.group(containing: session.id)?.id }
            // A refused split falls back to a tab; neither may leave a window-wide alert behind.
            let previous = error
            pane = addSession(kind: "shell", targetGroupId: group, placement: "right", workspaceId: workspaceId)
            if pane == nil { error = previous; pane = addSession(kind: "shell", targetGroupId: group, placement: "tab", workspaceId: workspaceId) }
            error = previous
        }
        guard let pane else { failBackgroundLogin(provider, id: id, message: L("loginRecovery.startFailed")); return }
        geminiLoginTerminal = pane
        let service = cliAccountService
        // Taken before the CLI starts, so its own write counts as new.
        let baseline = service.geminiCredentialsStamp()
        if reused == nil {
            updateSession(pane) { $0.title = L("loginRecovery.terminalTitle", ["provider": ProviderOptions.label(provider)]) }
            // The app wrote this command itself, so it presses Enter for the user (§1.5).
            pendingTerminalInput[pane] = TerminalInput(text: command, autoRun: true)
        }
        backgroundLogins[provider]?.phase = .waiting
        backgroundLogins[provider]?.terminalSessionId = pane
        job.task = Task { [weak self] in
            let outcome = await CLIGeminiLogin.wait(baseline: baseline, stamp: { service.geminiCredentialsStamp() },
                                                    status: { await service.status(provider: provider) },
                                                    isOpen: { [weak self] in await self?.terminalPaneOpen(pane) ?? false },
                                                    interval: Self.cliLoginPollInterval, limit: Self.cliLoginPollLimit)
            guard let self, self.backgroundLoginJobs[provider]?.id == id else { return }
            switch outcome {
            case .loggedIn(let status):
                self.autoLoginGate.succeeded(provider: provider)
                self.backgroundLoginJobs.removeValue(forKey: provider)
                await self.loginRestored(provider, status: status)
            case .exited(let status):
                self.cliAccounts[provider] = status
                self.failBackgroundLogin(provider, id: id, message: L("loginRecovery.exited"))
            case .timedOut(let status):
                self.cliAccounts[provider] = status
                self.failBackgroundLogin(provider, id: id, message: L("loginRecovery.timedOut"))
            case .cancelled: break
            }
        }
    }

    /// Adds the sign-in terminal beside `failed` the way an agent's own
    /// terminal pane opens, but leaves the selection, the workspace and the
    /// layout mode as they were. Nil when the layout has no place for it.
    private func insertLoginTerminal(beside failed: RunSession) -> String? {
        guard snapshot.sessions.count < 128 else { return nil }
        let session = RunSession(workspaceId: failed.workspaceId, title: L("loginRecovery.terminalTitle", ["provider": ProviderOptions.label("gemini")]), kind: "shell")
        reconcilePaneLayout(failed.workspaceId)
        let root = layoutForWorkspace(failed.workspaceId)
        let group = root?.group(containing: failed.id)
        var next = PaneLayouts.inserting(root: root, sessionId: session.id, targetGroupId: group?.id, placement: "right")
        if next?.group(containing: session.id) == nil {
            // A new tab is selected on insert; keep showing what the group showed.
            next = PaneLayouts.selecting(root: PaneLayouts.inserting(root: root, sessionId: session.id, targetGroupId: group?.id, placement: "tab"), id: group?.selectedSessionId ?? failed.id)
        }
        guard let next, next.group(containing: session.id) != nil else { return nil }
        snapshot.sessions.append(session)
        savePaneLayout(next, workspaceId: failed.workspaceId)
        return session.id
    }

    private func terminalPaneOpen(_ id: String) -> Bool {
        !closingSessions.contains(id) && snapshot.sessions.contains { $0.id == id }
    }

    /// The fallback after a failed background sign-in: the terminal-pane
    /// sign-in from Settings. It cannot see its command end, so it resends by
    /// itself only when the status went from signed out to signed in; else the
    /// card's resend button does it.
    func startTerminalLoginFallback(_ provider: String) {
        cancelBackgroundLogin(provider)
        startCLILogin(provider)
    }

    /// Signed in again: refresh what the app knows about the provider, clear
    /// its cards, then resend each waiting request whose pane is still idle
    /// on that provider. A pane that sent anything since lost its retry then.
    func loginRestored(_ provider: String, status: CLIAccountStatus) async {
        cliAccounts[provider] = status
        autoLoginGate.succeeded(provider: provider)
        backgroundLogins.removeValue(forKey: provider)
        for (id, value) in loginRequired where value == provider { loginCardNotes.removeValue(forKey: id) }
        loginRequired = loginRequired.filter { $0.value != provider }
        await cliAccountService.invalidateEnvironment()
        await refreshRuntimeAfterCLIUpdate()
        guard !ending else { return }
        let retries = loginRetries.take(provider: provider) { id in
            guard !closingSessions.contains(id), let session = snapshot.sessions.first(where: { $0.id == id }) else { return .gone }
            if session.status == "running" || pendingRuns.contains(id) { return .busy }
            return .idle(provider: session.provider)
        }
        for request in retries { resendLoginRequest(request) }
    }

    /// The card's resend button: also for a sign-in made in the user's own terminal.
    func resendLoginRequestNow(_ id: String) {
        guard let request = loginRetries.take(sessionId: id) else { return }
        loginCardNotes.removeValue(forKey: id)
        resendLoginRequest(request)
    }

    /// Resends one failed request. Work that holds runs back for a while (a
    /// CLI or plugin update, a model reset, a plugin change) keeps it until that
    /// work ends; any other refusal keeps it on the pane's card with the reason.
    /// With --resume the resent prompt can appear twice in the transcript, just
    /// as when the user sends it again by hand; that is accepted.
    @discardableResult
    func resendLoginRequest(_ request: CLILoginRetryRequest) -> Bool {
        guard !ending, !closingSessions.contains(request.sessionId),
              let session = snapshot.sessions.first(where: { $0.id == request.sessionId }), session.provider == request.provider,
              session.status != "running", !pendingRuns.contains(session.id),
              let workspace = snapshot.workspaces.first(where: { $0.id == session.workspaceId }) else { return false }
        if waitsForBackgroundWork(session.provider) {
            loginRetries.remember(request); awaitingLoginResend.insert(session.id); return false
        }
        // Never while a new sign-in runs: its success resends what waits.
        if backgroundLoginJobs[request.provider] != nil {
            loginRetries.remember(request); loginRequired[session.id] = request.provider; return false
        }
        func keep(_ reason: String) {
            loginRetries.remember(request)
            loginRequired[session.id] = request.provider
            loginCardNotes[session.id] = reason
        }
        if let reason = runBlockedReason(session) { keep(reason); return false }
        let previous = error
        guard start(session.id, session: session, workspace: workspace, input: request.input, attachments: request.attachments, restoringDraft: nil) else {
            // The card shows why; the window-wide alert stays as it was.
            let reason = error ?? L("loginRecovery.resendFailed")
            error = previous
            keep(reason); return false
        }
        // Its own sign-in failure, if any, only raises the card again.
        inFlightRequests[session.id]?.request.resent = true
        updateSession(session.id) { $0.logs.append(LogEntry(kind: "system", text: L("loginRecovery.resent"))) }
        return true
    }

    /// Work that refuses new runs of `provider` for a while, then ends.
    func waitsForBackgroundWork(_ provider: String) -> Bool {
        updatingCLI == provider || updatingPluginsFor == provider
            || (isManagingPlugins && ["claude", "codex"].contains(provider))
            || (claudeModelResetInProgress && provider == "claude")
    }

    /// Called when an update, a model reset or a plugin change ends.
    func resendAwaitingLoginRequests() {
        for id in awaitingLoginResend.sorted() {
            awaitingLoginResend.remove(id)
            guard let request = loginRetries.take(sessionId: id) else { continue }
            resendLoginRequest(request)
        }
    }
}
