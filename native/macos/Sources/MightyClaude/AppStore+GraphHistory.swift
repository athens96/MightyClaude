import Foundation
import MightyCore

extension AppStore {
    /// The older requests to draw above a pane's retained ones, and the state
    /// of loading more. Loaded runs that no longer attach above the retained
    /// list (the pane resumed another session) are not shown; the next load
    /// starts over.
    func graphHistory(for session: RunSession, retained: [MightyGraphRun]) -> SessionHistoryState {
        guard let state = graphHistory[session.id],
              state.connects(anchorRunID: retained.first?.id, resumeID: session.resumeId) else { return SessionHistoryState() }
        return state
    }

    /// Keeps a pane's loaded history attached while its retained list
    /// changes: runs a trim drops move into the history, and a pane that
    /// loaded before its first request anchors on that request.
    func followGraphHistory(_ session: RunSession, previous: [MightyGraphRun]) {
        let current = session.graphRuns ?? []
        guard previous.first?.id != current.first?.id, var state = graphHistory[session.id] else { return }
        state.follow(previous: previous, current: current, resumeID: session.resumeId, provider: session.provider)
        if state != graphHistory[session.id] { graphHistory[session.id] = state }
    }

    /// Reads the next older chunk of the pane's session record off the main
    /// actor and puts its requests above the ones already shown. A load
    /// already in flight, or a record whose start is on screen, does nothing.
    func loadOlderGraphHistory(_ id: String) {
        guard let session = snapshot.sessions.first(where: { $0.id == id }), session.kind == "claude",
              SessionHistory.providers.contains(session.provider),
              let workspace = snapshot.workspaces.first(where: { $0.id == session.workspaceId }) else { return }
        let retained = session.mightyGraphRuns
        let first = retained.first
        let sent = first.flatMap { run in session.logs.first(where: { $0.id == run.id })?.timestamp ?? run.rootEntries.first?.timestamp }
        let anchor = first.map { SessionHistoryAnchor(text: $0.input, date: sent.flatMap(AgentRunTiming.parseTimestamp)) }
        var state = graphHistory[id] ?? SessionHistoryState()
        let base = SessionHistoryRequest(provider: session.provider, resumeID: session.resumeId ?? "", workspacePath: workspace.path,
                                         environment: ProviderService.runtimeEnvironment())
        let request = state.begin(anchorRunID: first?.id, resumeID: session.resumeId, anchor: anchor, base: base)
        graphHistory[id] = state
        guard let request else { return }
        let generation = state.generation
        // Cancelled when the pane closes; the reader stops between requests.
        let work = Task.detached(priority: .utility) { Result { try SessionHistory.load(request) } }
        graphHistoryLoads[id] = work
        Task { [weak self] in
            let result = await work.value
            guard let self else { return }
            if self.graphHistoryLoads[id] == work { self.graphHistoryLoads.removeValue(forKey: id) }
            guard !work.isCancelled, !self.closingSessions.contains(id), self.snapshot.sessions.contains(where: { $0.id == id }),
                  var current = self.graphHistory[id] else { return }
            let restart = current.finish(result, generation: generation)
            self.graphHistory[id] = current
            // The record was replaced under the cursor: start over from its end.
            if restart { self.loadOlderGraphHistory(id) }
        }
    }

    /// Drops a closing pane's loaded history and stops a load still reading.
    func forgetGraphHistory(_ id: String) {
        graphHistory.removeValue(forKey: id)
        graphHistoryLoads.removeValue(forKey: id)?.cancel()
    }
}
