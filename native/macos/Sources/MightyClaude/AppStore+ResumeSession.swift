import Foundation
import MightyCore

/// The "창 추가" step after picking an agent that can continue an earlier session:
/// first "새로 시작" or "이어가기", then that agent's session list. One sheet holds
/// every step, so going on never presents a second sheet.
struct ResumePickerRequest: Identifiable {
    enum Stage {
        /// The look-up outlasted `AddAgentPane.quietLookUp`: the sheet waits for it.
        case checking
        case choice
        case list
    }
    let workspace: Workspace
    /// The agent picked in "창 추가"; the list shows only its sessions.
    let provider: String
    var stage: Stage
    /// Stays the same across the steps, so the sheet changes its content in place.
    var id: String { workspace.id + ":" + provider }
}

extension AppStore {
    /// An agent entry of the "창 추가" menu. An agent whose sessions the app can
    /// continue asks "새로 시작" or "이어가기" when this folder has one of its sessions
    /// to continue (`AddAgentPane`); otherwise, and for every other agent, the pane is
    /// added at once. A look-up longer than `AddAgentPane.quietLookUp` opens the
    /// sheet in its checking state, which the answer then fills or closes.
    func addAgentPane(provider: String, workspaceId: String) {
        guard !hasModal, let workspace = snapshot.workspaces.first(where: { $0.id == workspaceId }) else { return }
        guard AddAgentPane.offersResume(provider) else { startAgentPane(provider: provider, workspaceId: workspaceId); return }
        // One look-up at a time; a second click while it runs is the same request.
        guard addPaneProbe == nil else { return }
        let token = UUID()
        let task = Task { [weak self] in
            guard let self else { return }
            let reveal = Task { [weak self] in
                try? await Task.sleep(for: AddAgentPane.quietLookUp)
                guard let self, !Task.isCancelled, self.addPaneProbe?.token == token, !self.hasModal else { return }
                self.resumePicker = ResumePickerRequest(workspace: workspace, provider: provider, stage: .checking)
            }
            let found = await self.resumableSessions(for: workspace, provider: provider, probe: true)
            reveal.cancel()
            self.finishAddPaneProbe(token, workspace: workspace, provider: provider, found: found)
        }
        addPaneProbe = (token, task)
    }

    private func finishAddPaneProbe(_ token: UUID, workspace: Workspace, provider: String, found: ResumableSessionListing) {
        // Cancelled: the user switched workspace or to the dashboard, started a pane,
        // removed the workspace, or closed the checking sheet.
        guard addPaneProbe?.token == token, !Task.isCancelled else { return }
        addPaneProbe = nil
        let checking = resumePicker.map { $0.stage == .checking && $0.id == workspace.id + ":" + provider } ?? false
        guard checking || !hasModal, snapshot.workspaces.contains(where: { $0.id == workspace.id }) else {
            if checking { resumePicker = nil }
            return
        }
        switch AddAgentPane.step(provider: provider, sessions: found.items, inUse: ResumableSessions.inUse(snapshot.sessions)) {
        case .startNew:
            resumePicker = nil
            startAgentPane(provider: provider, workspaceId: workspace.id)
        case .askResumeOrNew:
            if checking { resumePicker?.stage = .choice }
            else { resumePicker = ResumePickerRequest(workspace: workspace, provider: provider, stage: .choice) }
        }
    }

    /// Stops a running "창 추가" look-up so its answer is dropped, and closes the
    /// sheet if it was still checking.
    func cancelAddPaneProbe() {
        guard let probe = addPaneProbe else { return }
        probe.task.cancel()
        addPaneProbe = nil
        if resumePicker?.stage == .checking { resumePicker = nil }
    }

    /// "새로 시작" (also while still checking): closes the step and adds a new pane
    /// of the picked agent.
    func startNewFromResumeChoice() {
        guard let request = resumePicker else { return }
        cancelAddPaneProbe()
        resumePicker = nil
        startAgentPane(provider: request.provider, workspaceId: request.workspace.id)
    }

    /// "취소" or Esc on the choice: no pane.
    func closeResumeChoice() {
        cancelAddPaneProbe()
        resumePicker = nil
    }

    /// "이어가기": the same sheet goes on to the agent's session list.
    func showResumeList() {
        guard resumePicker?.stage == .choice else { return }
        resumePicker?.stage = .list
    }

    private func startAgentPane(provider: String, workspaceId: String) {
        guard snapshot.workspaces.contains(where: { $0.id == workspaceId }) else { return }
        selectWorkspace(workspaceId)
        addSession(kind: SessionKind.claude, provider: provider)
    }

    /// Sessions of `provider` recorded for the workspace folder that no open
    /// pane uses, read off the main actor; cancelling the caller stops the scan.
    /// Automated runs are left out (and counted) unless `includeAutomated`.
    /// `probe`: stop at the first one and read only record heads.
    func resumableSessions(for workspace: Workspace, provider: String, includeAutomated: Bool = false, probe: Bool = false) async -> ResumableSessionListing {
        let query = ResumableSessionQuery(workspacePath: workspace.path, environment: ProviderService.runtimeEnvironment(),
                                          excluding: ResumableSessions.inUse(snapshot.sessions), known: Set(knownSessions()),
                                          includeAutomated: includeAutomated)
        let scan = probe ? AddAgentPane.probe(query) : query
        let reading = Task.detached(priority: .userInitiated) { ResumableSessions.listing(scan, provider: provider) }
        return await withTaskCancellationHandler { await reading.value } onCancel: { reading.cancel() }
    }

    /// Closes the picker and adds an agent pane that continues `item`. A
    /// session another pane took since the list was read is refused, so two
    /// panes never resume the same session.
    @discardableResult
    func resumeSession(_ item: ResumableSession, workspaceId: String) -> String? {
        resumePicker = nil
        guard ResumableSessions.providers.contains(item.provider), CoreValidation.identifier(item.sessionID),
              snapshot.workspaces.contains(where: { $0.id == workspaceId }) else { return nil }
        guard !ResumableSessions.inUse(snapshot.sessions).contains(item.sessionID.lowercased()) else {
            error = L("resume.error.inUse"); return nil
        }
        selectWorkspace(workspaceId)
        guard let id = addSession(kind: SessionKind.claude, provider: item.provider, workspaceId: workspaceId) else { return nil }
        updateSession(id) { ResumableSessions.apply(item, to: &$0) }
        rememberSessionID(item.sessionID)
        // The Mighty diagram shows the session's latest requests right away;
        // a pane in the default view loads them when switched to Mighty.
        if snapshot.sessions.first(where: { $0.id == id })?.agentViewMode == "mighty" { loadOlderGraphHistory(id) }
        return id
    }

    // MARK: Sessions the app's panes use

    private var knownSessionsURL: URL { dataDirectory.appendingPathComponent("known-sessions.json") }

    /// Ids of sessions the app's panes started or resumed, read once.
    func knownSessions() -> [String] {
        if let ids = knownSessionIDs { return ids }
        let ids = KnownSessionIDs.load(knownSessionsURL)
        knownSessionIDs = ids
        return ids
    }

    /// Remembers a session a pane started or resumed, so the session list
    /// never hides it as an automated run.
    func rememberSessionID(_ id: String) {
        guard let next = KnownSessionIDs.adding(id, to: knownSessions()) else { return }
        knownSessionIDs = next
        do { try KnownSessionIDs.save(next, to: knownSessionsURL) }
        catch { NSLog("MightyClaude known session ids not saved: %@", error.localizedDescription) }
    }
}
