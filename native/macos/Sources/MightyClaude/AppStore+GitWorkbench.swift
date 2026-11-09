import Foundation
import MightyCore

extension AppStore {
    /// The workspace's agent pane the user used last (not a delegated child),
    /// where the commit sheet and other workspace tools put a request they draft.
    func newestAgentPane(workspaceId: String) -> RunSession? {
        snapshot.sessions
            .filter { $0.workspaceId == workspaceId && $0.kind == SessionKind.claude && $0.parentSessionId == nil }
            .max { $0.lastUsedAt == $1.lastUsedAt ? $0.createdAt < $1.createdAt : $0.lastUsedAt < $1.lastUsedAt }
    }

    /// Adds `text` after the draft of the workspace's newest agent pane; the pane's title, or nil when there is none.
    @discardableResult
    func appendToAgentDraft(workspaceId: String, text: String) -> String? {
        guard let pane = newestAgentPane(workspaceId: workspaceId) else { return nil }
        drafts[pane.id] = ReviewNotes.appending(text, to: drafts[pane.id] ?? "")
        return pane.title
    }
}
