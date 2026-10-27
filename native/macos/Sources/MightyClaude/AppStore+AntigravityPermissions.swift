import Foundation
import MightyCore

/// Antigravity permission cards (`AntigravityPermissions.swift`): a headless
/// agy turn is refused what it would have asked about, so the pane asks once
/// the turn is over. An allow puts rules in agy's settings for a follow-up
/// turn in the same conversation; a remote pane's host does that on its own
/// computer, carried by the follow-up's start (`StartRunRequest.antigravityGrant`).
@MainActor
extension AppStore {
    /// A card arrives before its run's final status; a later run's end clears it.
    func receiveAntigravityEvent(_ event: RunEvent) {
        guard !ending else { return }
        if event.type == "antigravityPermission", let card = event.antigravityPermission, !card.actions.isEmpty,
           let session = snapshot.sessions.first(where: { $0.id == event.sessionId }), session.kind == "claude", session.provider == "gemini" {
            antigravityBoard.received(card, sessionId: event.sessionId)
            antigravityPermissionErrors.removeValue(forKey: event.sessionId)
        }
        guard event.type == "status", let status = event.status, ["completed", "error", "stopped"].contains(status) else { return }
        antigravityBoard.runEnded(sessionId: event.sessionId)
        if let lease = antigravityLeases.removeValue(forKey: event.sessionId) {
            Task { await antigravityRules.release(lease) }
        }
    }

    func antigravityResponseKey(sessionId: String, card: AntigravityPermissionRequest) -> String {
        "\(sessionId)|\(card.runId)|\(card.id)"
    }

    /// The card's buttons, and the phone's answer. `deny` closes the card. An
    /// allow sends the continuation on the pane's conversation now, or queues
    /// it behind a busy pane, with the card's rules: put in place while that
    /// run goes (`once`) or for good (`always`). Returns why nothing happened,
    /// which the card shows too; nil when it went through. `fromThisMac`: the card's own
    /// buttons here, whose continuation the pane's views move to (natural submit B2);
    /// false for the phone's answer.
    @discardableResult
    func answerAntigravityPermission(sessionId: String, request: AntigravityPermissionRequest, decision: AntigravityPermissionDecision, fromThisMac: Bool = true) async -> String? {
        let key = antigravityResponseKey(sessionId: sessionId, card: request)
        guard !ending, !closingSessions.contains(sessionId), !antigravityResponses.contains(key),
              let card = antigravityBoard.pending(sessionId: sessionId, requestId: request.id, runId: request.runId) else { return L("remote.error.notPendingPermission") }
        antigravityPermissionErrors.removeValue(forKey: sessionId)
        guard decision != .deny else { antigravityBoard.remove(sessionId: sessionId); return nil }
        func refuse(_ reason: String) -> String { antigravityPermissionErrors[sessionId] = reason; return reason }
        // No rule, or "항상 허용" for a rule that may be allowed only once.
        if let reason = AntigravityPermissionFlow.refusal(card, decision: decision) { return refuse(reason) }
        guard let continuation = AntigravityPermissionFlow.continuation(card, decision: decision) else { return refuse(L("antigravity.permission.noRule")) }
        antigravityResponses.insert(key)
        defer { antigravityResponses.remove(key) }
        // A local pane's rules go in this Mac's settings: one that cannot take
        // them is refused here, before anything is sent.
        if !isRemoteSession(sessionId) {
            do { try await antigravityRules.check() } catch { return refuse(error.localizedDescription) }
        }
        guard !ending, !closingSessions.contains(sessionId), antigravityBoard.pending(sessionId: sessionId, requestId: card.id, runId: card.runId) != nil,
              let session = snapshot.sessions.first(where: { $0.id == sessionId }), session.kind == "claude", session.provider == "gemini",
              let workspace = snapshot.workspaces.first(where: { $0.id == session.workspaceId }) else { return L("remote.error.notPendingPermission") }
        if let reason = runBlockedReason(session) { return refuse(reason) }
        let item = QueuedInput(text: continuation.prompt, antigravityGrant: continuation.grant, antigravityCard: card, own: fromThisMac)
        let held = backgroundUpdateHolds(session)
        if session.status == "running" || pendingRuns.contains(sessionId) || held {
            // The composer's rule for a busy pane: it waits in the queue.
            if held { heldForUpdate.insert(sessionId) }
            guard case .queued = deferInput(sessionId, session: session, workspace: workspace, item: item, takingDraft: false) else {
                return refuse(L("queue.fullCount", ["count": "\(QueuedInput.maximumItems)"]))
            }
            antigravityBoard.remove(sessionId: sessionId)
            return nil
        }
        antigravityBoard.remove(sessionId: sessionId)
        guard start(sessionId, session: session, workspace: workspace, input: item.text, attachments: [], restoringDraft: nil, queued: item, own: item.own) else {
            antigravityBoard.restore(card, sessionId: sessionId)
            return refuse(error ?? L("remote.error.notPendingPermission"))
        }
        if await startReachedRunner(sessionId) { return nil }
        // It did not start (agy's settings refused the rules meanwhile, a
        // remote host said no): the card comes back with the reason.
        let reason = snapshot.sessions.first { $0.id == sessionId }?.logs.last { $0.kind == "error" }?.text ?? L("remote.error.notPendingPermission")
        guard !ending, !closingSessions.contains(sessionId), snapshot.sessions.contains(where: { $0.id == sessionId }) else { return reason }
        antigravityBoard.restore(card, sessionId: sessionId)
        return refuse(reason)
    }
}
