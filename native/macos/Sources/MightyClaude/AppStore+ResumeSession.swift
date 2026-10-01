import Foundation
import MightyCore

extension AppStore {
    func openResumePicker(_ workspaceId: String) {
        guard !hasModal, let workspace = snapshot.workspaces.first(where: { $0.id == workspaceId }) else { return }
        resumePickerWorkspace = workspace
    }

    /// Sessions recorded for the workspace folder that no open pane uses,
    /// read off the main actor. Automated runs are left out (and counted)
    /// unless `includeAutomated`.
    func resumableSessions(for workspace: Workspace, includeAutomated: Bool = false) async -> ResumableSessionListing {
        let query = ResumableSessionQuery(workspacePath: workspace.path, environment: ProviderService.runtimeEnvironment(),
                                          excluding: ResumableSessions.inUse(snapshot.sessions), known: Set(knownSessions()),
                                          includeAutomated: includeAutomated)
        return await Task.detached(priority: .userInitiated) { ResumableSessions.listing(query) }.value
    }

    /// Closes the picker and adds an agent pane that continues `item`. A
    /// session another pane took since the list was read is refused, so two
    /// panes never resume the same session.
    @discardableResult
    func resumeSession(_ item: ResumableSession, workspaceId: String) -> String? {
        resumePickerWorkspace = nil
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
