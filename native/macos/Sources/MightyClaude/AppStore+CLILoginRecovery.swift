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
        if session.kind == "claude", ["claude", "codex"].contains(session.provider) {
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
        startBackgroundLogin(provider, automatic: true)
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

    func startBackgroundLogin(_ provider: String, automatic: Bool = false) {
        guard !ending, ["claude", "codex"].contains(provider), backgroundLoginJobs[provider] == nil,
              let command = CLIAccountSupport.loginCommand(provider: provider, option: .account) else { return }
        let id = UUID()
        if let reason = accountBusyReason(provider) {
            backgroundLogins[provider] = BackgroundLoginState(id: id, phase: .failed(reason)); return
        }
        cliAccountMessages[provider] = nil
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
