import AppKit
import Foundation
import GhosttyTerminal
import MightyCore

extension AppStore {
    var terminalSmokeMode: Bool { ProcessInfo.processInfo.arguments.contains("--terminal-smoke-test") }
    private var isolatedTerminalSmoke: Bool { terminalSmokeMode || ProcessInfo.processInfo.arguments.contains("--layout-smoke-test") }

    func usesLocalTerminal(_ session: RunSession) -> Bool {
        guard session.kind == "shell", let workspace = snapshot.workspaces.first(where: { $0.id == session.workspaceId }), workspace.remote == nil else { return false }
        return !ProcessInfo.processInfo.arguments.contains("--smoke-test") || terminalSmokeMode
    }

    func ensureLocalTerminal(_ id: String) async {
        guard canEditAttachments(id), let session = snapshot.sessions.first(where: { $0.id == id }), usesLocalTerminal(session), localTerminals[id] == nil,
              terminalStarts.insert(id).inserted else { return }
        defer { terminalStarts.remove(id) }
        terminalErrors.removeValue(forKey: id)
        do {
            let workspace = try await repository.resolveLocalWorkspace(id: session.workspaceId)
            try Task.checkCancellation()
            guard canEditAttachments(id), localTerminals[id] == nil else { return }
            let controller = sharedTerminalController()
            let terminal = LocalTerminalSession(id: id, directory: workspace.path, controller: controller, smoke: isolatedTerminalSmoke, statusChanged: { [weak self] status in
                self?.updateSession(id) { $0.status = status }
            }, focused: { [weak self] in
                guard self?.snapshot.activeSessionId != id else { return }
                self?.selectSession(id)
            }, closeRequested: { [weak self] in self?.closeSession(id) })
            localTerminals[id] = terminal
        } catch {
            if !Task.isCancelled { terminalErrors[id] = error.localizedDescription; updateSession(id) { $0.status = "error" } }
        }
    }

    func restartTerminal(_ id: String) async {
        disposeTerminal(id)
        updateSession(id) { $0.status = "idle" }
        await ensureLocalTerminal(id)
    }

    func disposeTerminal(_ id: String) {
        localTerminals.removeValue(forKey: id)?.dispose()
        terminalErrors.removeValue(forKey: id)
    }

    func shutdownTerminals() {
        terminalTick?.cancel()
        terminalTick = nil
        for terminal in localTerminals.values { terminal.dispose() }
        localTerminals.removeAll()
        terminalController = nil
    }

    private func sharedTerminalController() -> TerminalController {
        if let terminalController { return terminalController }
        let theme = TerminalTheme(
            light: TerminalConfiguration.alabaster.background("FAF9F6"),
            dark: TerminalConfiguration.afterglow.background("202020").foreground("DEDCD8").cursorColor("E1AB91")
        )
        let shellCommand = isolatedTerminalSmoke ? "/bin/zsh -f" : "/bin/zsh -il"
        let controller = TerminalController(theme: theme) { settings in
            settings.withCustom("command", shellCommand)
            settings.withCustom("wait-after-command", "false")
            settings.withFontFamily("Menlo")
            settings.withFontSize(12)
            settings.withWindowPaddingX(10)
            settings.withWindowPaddingY(10)
            settings.withCustom("clipboard-read", "deny")
            settings.withCustom("clipboard-write", "ask")
            settings.withCustom("scrollback-limit", "10000000")
            // These shortcuts belong to the host menu. Ghostty's built-in
            // new-tab/window actions otherwise consume them without a host.
            for key in ["t", "n", "o", "k", "q", "comma"] {
                settings.withCustom("keybind", "super+\(key)=unbind")
            }
        }
        terminalController = controller
        // The package suspends wakeup delivery for detached views. Drain the
        // shared mailbox so background PTYs can still report cwd, exit, and
        // output without filling it while another workspace is displayed.
        terminalTick = Task { [weak self, weak controller] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .milliseconds(100))
                guard !Task.isCancelled, let self, let controller else { return }
                if !self.localTerminals.isEmpty { controller.tick() }
            }
        }
        return controller
    }
}
