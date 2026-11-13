import Foundation
import MightyCore

/// The phone's side of the panes that work in their own worktree (docs/mobile-remote.md, "창마다 worktree"): a new pane
/// says whether it got one, and `POST …/worktree` runs the bar's actions. The Mac's git work is the bar's
/// (`AppStore+PaneWorktree.swift`); the phone confirms a discard itself, so no alert is shown here.
extension AppStore {
    /// `POST /m1/workspaces/{id}/sessions` with what the phone shows afterwards: the pane works in its own worktree (and
    /// where), or the workspace's setting chose a worktree that could not be made and the pane works in the folder (why).
    func mobileCreateSessionResult(workspaceId: String, kind: String, provider: String) async throws -> MobileCreatedSession {
        let wanted = kind == SessionKind.claude && snapshot.workspaces.first(where: { $0.id == workspaceId && $0.remote == nil }).map {
            PaneWorktreePolicy.decideWithoutAsking(workspace: $0, sessions: snapshot.sessions, closing: closingSessions) == .worktree
        } == true
        let id = try await mobileCreateSession(workspaceId: workspaceId, kind: kind, provider: provider)
        guard let session = snapshot.sessions.first(where: { $0.id == id }) else { return MobileCreatedSession(sessionId: id) }
        return MobileCreatedSession(sessionId: id, worktree: session.paneWorktree == nil ? nil : true,
                                    notice: Self.mobileCreatedNotice(session, wantedWorktree: wanted))
    }

    /// The created pane's line: its worktree's, or — a worktree wanted and not made — the reason its transcript gives.
    static func mobileCreatedNotice(_ session: RunSession, wantedWorktree: Bool) -> String? {
        if let pane = session.paneWorktree {
            return L("paneWorktree.log.made", ["branch": pane.branch, "base": pane.baseBranch, "port": String(pane.port)])
        }
        guard wantedWorktree else { return nil }
        return session.logs.last(where: { $0.kind == "system" })?.text
    }

    /// One of the bar's actions from the phone. `refresh` reads the counts again; the rest run as the bar's and answer
    /// what the pane's transcript says.
    func mobilePaneWorktree(sessionId: String, action: String) async throws -> MobilePaneWorktreeResult {
        guard let session = snapshot.sessions.first(where: { $0.id == sessionId }) else { throw MobileHostError.notFound(L("remote.error.paneNotFound")) }
        guard session.paneWorktree != nil else { throw MobileHostError.conflict(L("paneWorktree.refused.worktreeMissing")) }
        guard !hasBlockingModal else { throw MightyError(L("remote.error.closeMacWindow")) }
        let outcome: PaneWorktreeActionOutcome?
        switch action {
        case "refresh":
            await refreshPaneWorktreeStatus(sessionId)
            return MobilePaneWorktreeResult(done: true)
        case "merge": outcome = await runPaneWorktreeMerge(sessionId, commitFirst: false)
        case "commitMerge": outcome = await runPaneWorktreeMerge(sessionId, commitFirst: true)
        case "undo": outcome = await runPaneWorktreeUndo(sessionId)
        case "resolveConflict": outcome = await runPaneConflictRequest(sessionId)
        case "discard": outcome = await runPaneWorktreeDiscard(sessionId)
        default: throw MobileHostError.badRequest(L("remote.error.badAction"))
        }
        // Nil: another action of the pane is under way (its worktree was there a moment ago).
        guard let outcome else { throw MobileHostError.conflict(L("delegation.card.working")) }
        return MobilePaneWorktreeResult(done: outcome.done, message: outcome.message)
    }
}
