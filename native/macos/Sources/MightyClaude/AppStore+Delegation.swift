import Foundation
import MightyCore

/// The app side of parent → child delegation between Claude panes (macOS
/// only): AppStore is the coordinator's ``DelegationHost``. The coordinator
/// owns the records, the tools, delivery and the git actions; the store only
/// makes panes, starts and steers runs and reports what its panes do.
extension AppStore {
    /// Makes the coordinator from the profile's delegation file, whether or
    /// not the hidden switch is on: switching it off only detaches the
    /// delegation server, so existing children keep working. Worktrees go
    /// under `~/.mightyclaude/worktrees`, or the profile for smoke runs.
    func startDelegation() {
        guard delegation == nil else { return }
        let root = smokeTesting ? dataDirectory.appendingPathComponent("worktrees", isDirectory: true) : ChildWorktreeMaker.defaultRoot
        do {
            let coordinator = try DelegationCoordinator(store: DelegationFileStore(directory: dataDirectory), host: self, worktrees: ChildWorktreeMaker(root: root))
            delegation = coordinator
            delegationRunEvents = DelegationRunEventPump(coordinator: coordinator)
            // The sidebar's tree follows the file, switch on or off.
            delegationChildWatch = Task { [weak self] in
                for await rows in coordinator.childRows {
                    guard let self else { return }
                    if delegationChildren != rows { delegationChildren = rows }
                }
            }
        } catch {
            NSLog("MightyClaude delegation file unreadable; delegation is unavailable this launch: %@", error.localizedDescription)
        }
    }

    /// A run of the pane `id` ended with `status`. A child's end reaches the
    /// coordinator after its start; while the app quits it counts as killed by quitting.
    func delegationRunEnded(_ id: String, status: String) {
        guard let ended = delegationRuns.end(id, status: status, quitting: ending) else { return }
        guard snapshot.sessions.first(where: { $0.id == id })?.parentSessionId != nil else { return }
        delegationRunEvents?.send(.ended(childId: id, runId: ended.runId, end: ended.end))
    }

    // MARK: DelegationHost

    /// The child's Claude pane, as a tab beside its parent: in the mode the
    /// parent asked for, never the last-used pane's, linked to its parent and
    /// working in its worktree folder. Its title is its task, kept as the
    /// pane's name. The link is saved before its first run.
    func createPane(_ pane: DelegationChildPane) async -> Bool {
        guard !ending, isLoaded, snapshot.sessions.count < 128, !snapshot.sessions.contains(where: { $0.id == pane.sessionId }),
              let parent = snapshot.sessions.first(where: { $0.id == pane.parentSessionId }), !closingSessions.contains(parent.id),
              snapshot.workspaces.contains(where: { $0.id == parent.workspaceId }) else { return false }
        let file = await delegation?.file
        let task = file?.copy(childId: pane.sessionId, kind: .task)?.text
        var session = DelegationPanes.childSession(pane, workspaceId: parent.workspaceId, title: task.flatMap(PaneTitle.shortened) ?? ProviderOptions.label("claude"))
        session.titleMode = "fixed"
        // Checked again: the pane list may have changed while the task was read.
        guard !ending, !snapshot.sessions.contains(where: { $0.id == session.id }), snapshot.sessions.contains(where: { $0.id == parent.id }) else { return false }
        reconcilePaneLayout(parent.workspaceId)
        let root = layoutForWorkspace(parent.workspaceId)
        let group = root?.group(containing: parent.id)
        // A new tab is selected on insert; the group keeps showing what it showed.
        guard let next = PaneLayouts.selecting(root: PaneLayouts.inserting(root: root, sessionId: session.id, targetGroupId: group?.id, placement: "tab"),
                                               id: group?.selectedSessionId ?? parent.id),
              next.group(containing: session.id) != nil else { return false }
        let mode = paneLayoutMode(parent.workspaceId)
        snapshot.sessions.append(session)
        savePaneLayout(next, workspaceId: parent.workspaceId)
        if mode != "focus" { setPaneLayoutMode(next.kind == "split" ? "custom" : "tabs", workspaceId: parent.workspaceId) }
        do { try await flush() } catch { NSLog("MightyClaude could not save a new child pane: %@", error.localizedDescription) }
        return snapshot.sessions.contains { $0.id == session.id }
    }

    /// A run with `input` in the pane, the way a send starts one: its id, or
    /// nil when the pane cannot run now (a child without its worktree among them).
    func startRun(sessionId: String, input: String) async -> String? {
        guard !ending, let session = snapshot.sessions.first(where: { $0.id == sessionId }),
              let workspace = snapshot.workspaces.first(where: { $0.id == session.workspaceId }),
              start(sessionId, session: session, workspace: workspace, input: input, attachments: [], restoringDraft: nil) else { return nil }
        return delegationRuns.runId(sessionId)
    }

    /// `input` steered into the pane's running Claude run, shown in its
    /// transcript like a steered send, or started as its next run.
    func deliver(_ input: String, to sessionId: String, route: DeliveryRoute) async -> String? {
        switch route {
        case .queue:
            return await startRun(sessionId: sessionId, input: input)
        case .steer:
            guard !ending, !closingSessions.contains(sessionId), let session = snapshot.sessions.first(where: { $0.id == sessionId }),
                  session.status == "running", canSteer(session), let runId = delegationRuns.runId(sessionId) else { return nil }
            guard await steerRunningTurn(sessionId, text: input) else { return nil }
            updateSession(sessionId) { $0.logs.append(LogEntry(kind: "user", text: input)); $0.logs = TranscriptRetention.trimmed($0.logs) }
            return runId
        }
    }

    func paneState(sessionId: String) async -> DelegationPaneState? {
        guard !closingSessions.contains(sessionId), let session = snapshot.sessions.first(where: { $0.id == sessionId }),
              let workspace = snapshot.workspaces.first(where: { $0.id == session.workspaceId }) else { return nil }
        let running = session.status == "running" || pendingRuns.contains(sessionId)
        return DelegationPanes.paneState(of: session, in: workspace, runId: delegationRuns.runId(sessionId), activity: delegationRuns.activity(sessionId, running: running))
    }

    func stopRun(sessionId: String) async { await stop(sessionId) }
}
