import Foundation
import MightyCore

/// Claude's `statusLine` under the composer. The command is the user's own
/// (the CLI runs the same one), so the concerns are trust and containment:
/// workspace-level commands come from a repository and need a one-time
/// allow per workspace and command; runs are one per pane at a time,
/// throttled, capped, killed on timeout, and only for local Claude panes.
extension AppStore {
    struct StatusLineState: Equatable {
        var config: StatusLineConfig?
        /// A workspace-level command found but not yet allowed here.
        var untrusted: StatusLineConfig?
        var result: StatusLineResult?
        var updatedAt: Date?
        var running = false
        var pending = false
        var generation = 0
    }
    static let statusLineDefaultsKey = "statusLine.enabled"
    static let statusLineTrustKey = "statusLine.trusted"
    static let statusLineMinimumInterval: TimeInterval = 2

    var statusLineEnabled: Bool {
        get { UserDefaults.standard.object(forKey: Self.statusLineDefaultsKey) as? Bool ?? true }
        set {
            objectWillChange.send()
            UserDefaults.standard.set(newValue, forKey: Self.statusLineDefaultsKey)
            if !newValue { for id in statusLines.keys { statusLines[id]?.generation += 1 }; statusLines.removeAll() }
            else { for session in snapshot.sessions where session.workspaceId == snapshot.activeWorkspaceId { refreshStatusLine(for: session, force: true) } }
        }
    }

    /// workspace id → fingerprint of the workspace-level command the user allowed.
    private var trustedStatusLines: [String: String] {
        get { UserDefaults.standard.dictionary(forKey: Self.statusLineTrustKey) as? [String: String] ?? [:] }
        set { UserDefaults.standard.set(newValue, forKey: Self.statusLineTrustKey) }
    }
    func statusLineTrusted(_ config: StatusLineConfig, workspaceId: String) -> Bool {
        !config.fromWorkspace || trustedStatusLines[workspaceId] == config.fingerprint
    }
    /// Allows the workspace-level command shown in the prompt; an edited
    /// command changes the fingerprint and asks again.
    func trustStatusLine(_ config: StatusLineConfig, sessionID: String) {
        guard let session = snapshot.sessions.first(where: { $0.id == sessionID }) else { return }
        trustedStatusLines[session.workspaceId] = config.fingerprint
        for other in snapshot.sessions where other.workspaceId == session.workspaceId { refreshStatusLine(for: other, force: true) }
    }
    func dismissUntrustedStatusLine(sessionID: String) { statusLines[sessionID]?.untrusted = nil }

    /// Whether a pane shows a status line at all: local workspace, Claude
    /// provider, feature on, not a smoke run.
    func statusLineApplies(to session: RunSession) -> Bool {
        guard statusLineEnabled, !smokeTesting, session.kind == "claude", session.provider == "claude",
              let workspace = snapshot.workspaces.first(where: { $0.id == session.workspaceId }), workspace.remote == nil else { return false }
        return true
    }

    func refreshStatusLine(sessionID: String, force: Bool = false) {
        guard let session = snapshot.sessions.first(where: { $0.id == sessionID }) else { return }
        refreshStatusLine(for: session, force: force)
    }

    /// Re-runs the command unless one ran within the last two seconds; a
    /// request arriving during a run is remembered and served once it ends.
    func refreshStatusLine(for session: RunSession, force: Bool = false) {
        guard !ending, statusLineApplies(to: session),
              let workspace = snapshot.workspaces.first(where: { $0.id == session.workspaceId }) else {
            if statusLines[session.id] != nil { statusLines[session.id]?.generation += 1; statusLines.removeValue(forKey: session.id) }
            return
        }
        var state = statusLines[session.id] ?? StatusLineState()
        if state.running { state.pending = true; statusLines[session.id] = state; return }
        if !force, let updatedAt = state.updatedAt, Date().timeIntervalSince(updatedAt) < Self.statusLineMinimumInterval {
            guard !state.pending else { return }
            state.pending = true; statusLines[session.id] = state
            let delay = Self.statusLineMinimumInterval - Date().timeIntervalSince(updatedAt)
            Task { [weak self] in
                try? await Task.sleep(for: .seconds(delay))
                guard let self, let current = self.snapshot.sessions.first(where: { $0.id == session.id }) else { return }
                self.statusLines[session.id]?.pending = false
                self.refreshStatusLine(for: current, force: true)
            }
            return
        }
        state.running = true; state.pending = false; state.generation += 1
        let generation = state.generation
        statusLines[session.id] = state
        let context = statusLineContext(session, workspace: workspace)
        let environment = ProviderService.runtimeEnvironment()
        let workspaceId = workspace.id, cwd = workspace.path
        let trusted = trustedStatusLines[workspaceId]
        // Settings reads and the spawn stay off the main actor.
        Task.detached(priority: .utility) { [weak self] in
            let discovery = StatusLineConfig.discover(workspacePath: cwd)
            let gated = discovery.preferred.map { $0.fromWorkspace && trusted != $0.fingerprint } ?? false
            let config = gated ? discovery.user : discovery.preferred
            let untrusted = gated ? discovery.preferred : nil
            var payloadContext = context
            payloadContext.outputStyle = config?.outputStyle ?? discovery.user?.outputStyle
            payloadContext.thinkingEnabled = config?.thinkingEnabled ?? discovery.user?.thinkingEnabled
            let payload = StatusLineSupport.payload(payloadContext)
            let result: StatusLineResult? = if let config { await StatusLineSupport.run(config, payload: payload, cwd: cwd, environment: environment) } else { nil }
            await MainActor.run {
                guard let self, var current = self.statusLines[session.id], current.generation == generation else { return }
                current.config = config; current.untrusted = untrusted; current.result = result; current.updatedAt = Date(); current.running = false
                let rerun = current.pending; current.pending = false
                self.statusLines[session.id] = current
                if rerun, let session = self.snapshot.sessions.first(where: { $0.id == session.id }) { self.refreshStatusLine(for: session) }
            }
        }
    }

    func statusLineContext(_ session: RunSession, workspace: Workspace) -> StatusLineContext {
        let runtime = providerRuntime(session.provider, workspaceId: session.workspaceId)
        let usage = session.sessionUsage?.provider == session.provider ? session.sessionUsage : nil
        let option = runtime.modelCatalog.models.first { $0.value == session.model }
        let modelId = usage?.model ?? option?.resolvedModel ?? (session.model == "default" ? "default" : session.model)
        let modelName = option?.displayName ?? usage?.model ?? session.model
        let elapsed = session.runTiming?.elapsed() ?? 0
        return StatusLineContext(
            sessionId: session.resumeId ?? session.id, cwd: workspace.path, projectDir: workspace.path,
            modelId: modelId, modelName: modelName, version: runtime.version ?? "",
            costUSD: usage?.costUSD, durationMs: Int(max(0, elapsed) * 1000), apiDurationMs: 0,
            inputTokens: usage?.inputTokens, outputTokens: usage?.outputTokens, cacheReadTokens: usage?.cacheReadTokens, cacheWriteTokens: usage?.cacheWriteTokens,
            contextUsedTokens: usage?.contextUsedTokens, contextWindowTokens: usage?.contextWindowTokens,
            effort: session.settings.effort == "default" ? nil : session.settings.effort, fastMode: session.settings.fastMode,
            rateLimits: usage?.rateLimits ?? [])
    }
}
