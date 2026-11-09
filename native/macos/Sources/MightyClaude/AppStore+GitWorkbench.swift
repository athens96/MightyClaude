import AppKit
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

extension AppStore {
    /// A new terminal pane in the workspace with the task's command typed and
    /// waiting for Enter: it came from the project's files, so it never runs itself (§1.5).
    func typeProjectTask(_ task: ProjectTask, workspaceId: String) {
        guard let id = addSession(kind: SessionKind.shell, workspaceId: workspaceId) else { return }
        updateSession(id) { $0.title = task.name }
        pendingTerminalInput[id] = TerminalInput(text: task.command, autoRun: false)
    }

    /// Opens a local dev server's address where the workspace opens web pages
    /// (beside its newest agent pane), else in the default browser.
    func openListeningPort(_ port: ListeningPort, workspaceId: String) {
        guard let url = port.url else { return }
        Task {
            if let pane = newestAgentPane(workspaceId: workspaceId), await openInAgentBrowser(url, agentPaneId: pane.id) { return }
            NSWorkspace.shared.open(url)
        }
    }
}
