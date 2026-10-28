import Foundation
import MightyCore

/// The composer's "send to" switch on a Claude agent pane: another installed CLI
/// asked through oh-my-claudecode's ask skill (`AskTargets`). Which targets are
/// installed is read off the main actor with the slash catalogue, and published
/// only when it changed.
extension AppStore {
    /// Reads which target executables the login shell's PATH has, as the setup
    /// screen finds a CLI. At most every 30 s, like the slash catalogue.
    func refreshAskExecutables() {
        guard !ending, !helpCapturing, !askProbeInFlight else { return }
        if let at = askProbedAt, Date().timeIntervalSince(at) < 30 { return }
        askProbeInFlight = true
        let providers = providers
        Task.detached(priority: .utility) { [weak self] in
            let environment = await providers.executionEnvironment().values
            let found = Set(AskTargets.all.map(\.executable).filter { !SetupProber.executables($0, environment: environment).isEmpty })
            await MainActor.run {
                guard let self else { return }
                self.askProbeInFlight = false
                self.askProbedAt = Date()
                self.askProbeFinished = true
                if self.installedAskExecutables != found {
                    self.installedAskExecutables = found
                    self.mobileObserve()
                }
                self.reconcileAskTargets()
            }
        }
    }

    /// The targets the pane's switch offers, in the menu's order; empty hides it.
    /// A remote workspace's pane runs on another computer, whose CLIs this Mac cannot see.
    func askTargets(for session: RunSession) -> [AskTarget] {
        // Cheapest tests first: the composer asks on every render.
        let isClaudeAgent = session.kind == SessionKind.claude && session.provider == "claude" && !isRemoteWorkspace(session.workspaceId)
        guard isClaudeAgent, !installedAskExecutables.isEmpty else { return [] }
        // The scan already noted whether the skill is there; the list is not walked again here.
        let hasSkill = askCatalog(session)?.offersAsk == true
        return AskTargets.available(isClaudeAgent: isClaudeAgent, slashInvocations: hasSkill ? [AskTargets.skill] : [], installed: installedAskExecutables)
    }

    /// The target this pane's sends go to now; nil for Claude.
    func askSendTarget(_ session: RunSession) -> AskTarget? {
        guard session.askTarget != nil else { return nil }
        return AskTargets.effective(saved: session.askTarget, available: askTargets(for: session))
    }

    /// The text a send from this pane carries, for the composer and the phone
    /// alike; nil when nothing should be sent.
    func askSendText(_ text: String, session: RunSession) -> String? {
        AskTargets.wrap(text, target: askSendTarget(session))
    }

    /// Saves the pane's choice (nil: Claude). A target the pane does not offer is ignored.
    func setAskTarget(_ id: String, target: String?) {
        guard let session = snapshot.sessions.first(where: { $0.id == id }), session.askTarget != target else { return }
        if let target {
            guard askTargets(for: session).contains(where: { $0.id == target }) else { return }
            askFallbackNotices.removeValue(forKey: id)
        }
        updateSession(id) { $0.askTarget = target }
    }

    /// A saved target that is no longer offered (oh-my-claudecode or its CLI gone)
    /// reads as Claude: the pane says so once and the choice is cleared. Judged only
    /// after the PATH was read and the pane's catalogue scanned, so a launch never
    /// throws a good choice away before either is known.
    func reconcileAskTargets() {
        guard askProbeFinished else { return }
        for session in snapshot.sessions where session.askTarget != nil && askCatalog(session) != nil {
            guard askSendTarget(session) == nil, let saved = session.askTarget else { continue }
            let name = AskTargets.all.first { $0.id == saved }?.label ?? saved
            askFallbackNotices[session.id] = L("composer.ask.fellBack", ["name": name])
            setAskTarget(session.id, target: nil)
        }
    }

    /// The pane's scanned catalogue, as `slashCommands(for:)` finds it; nil before its first scan.
    private func askCatalog(_ session: RunSession) -> SlashCatalogEntry? {
        let workspace = snapshot.workspaces.first { $0.id == session.workspaceId }
        let path = workspace?.remote == nil ? workspace?.path : nil
        return slashCatalogs[Self.slashCatalogKey(provider: session.provider, workspacePath: path)]
    }
}
