import AppKit
import Foundation
import MightyCore

extension AppStore {
    /// A `mightyclaude://open` link: the folder's workspace is selected (a
    /// folder that is not one yet is added only after the user agrees), and
    /// the link's text goes into an agent pane's composer — never sent.
    func handleDeepLink(_ url: URL) {
        guard let link = DeepLink.parse(url) else { return }
        Task { @MainActor in
            // A link that launched the app waits for the saved state.
            for _ in 0..<100 where !isLoaded { try? await Task.sleep(for: .milliseconds(100)) }
            guard isLoaded, !hasModal else { return }
            var isFolder: ObjCBool = false
            guard FileManager.default.fileExists(atPath: link.path, isDirectory: &isFolder), isFolder.boolValue else {
                error = L("deepLink.error.noFolder", ["path": link.path]); return
            }
            NSApp.activate(ignoringOtherApps: true)
            if let workspace = snapshot.workspaces.first(where: { $0.remote == nil && URL(fileURLWithPath: $0.path).standardizedFileURL.path == link.path }) {
                selectWorkspace(workspace.id)
            } else {
                let alert = NSAlert()
                alert.messageText = L("deepLink.confirm.title")
                alert.informativeText = L("deepLink.confirm.message", ["path": link.path])
                alert.addButton(withTitle: L("deepLink.confirm.open"))
                alert.addButton(withTitle: L("runChanges.confirm.cancel"))
                guard alert.runModal() == .alertFirstButtonReturn else { return }
                do { try await addLocalWorkspace(path: link.path) } catch { self.error = error.localizedDescription; return }
            }
            guard let workspaceId = snapshot.activeWorkspaceId, let prompt = link.prompt else { return }
            let pane: String?
            if let agent = link.agent {
                pane = snapshot.sessions.first { $0.workspaceId == workspaceId && $0.kind == SessionKind.claude && $0.provider == agent && $0.parentSessionId == nil }?.id
                    ?? addSession(kind: SessionKind.claude, provider: agent, workspaceId: workspaceId)
            } else {
                pane = newestAgentPane(workspaceId: workspaceId)?.id ?? addSession(kind: SessionKind.claude, workspaceId: workspaceId)
            }
            guard let pane else { return }
            selectSession(pane)
            drafts[pane] = ReviewNotes.appending(prompt, to: drafts[pane] ?? "")
        }
    }

    /// Installs the `mighty` shell command and says where, and whether its folder is on the shell's PATH.
    func installShellCommand() {
        Task { @MainActor in
            do {
                let url = try ShellCommand.install()
                let path = await CLIEnvironmentResolver.shared.resolve().values["PATH"]
                let alert = NSAlert()
                alert.messageText = L("shellCommand.installed.title")
                alert.informativeText = ShellCommand.onPath(path, folder: ShellCommand.folder())
                    ? L("shellCommand.installed.ready", ["path": url.path])
                    : L("shellCommand.installed.addPath", ["path": url.path, "folder": ShellCommand.folder().path])
                alert.runModal()
            } catch { self.error = error.localizedDescription }
        }
    }
}

extension CompletionNotifications {
    /// Posts the finished run to the webhook the user set, if any (`CompletionWebhook`).
    static func postWebhook(title: String) {
        guard let text = UserDefaults.standard.string(forKey: CompletionWebhook.defaultsKey), let url = CompletionWebhook.validURL(text) else { return }
        let request = CompletionWebhook.request(url: url, message: L("windows.notifications.notificationBodyTemplate", ["title": title]))
        Task.detached { _ = try? await URLSession.shared.data(for: request) }
    }
}
