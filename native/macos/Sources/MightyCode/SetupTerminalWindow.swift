import AppKit
import GhosttyTerminal
import MightyCore
import SwiftUI

/// A terminal of its own window for what the setup screen runs (the install
/// script, or a sign-in when there is no workspace). It is never a pane: it is
/// not saved with the workspaces, not shown to the phone, never selected, and it
/// starts in the app's setup folder. Closing the window ends its shell.
@MainActor
final class SetupTerminalWindow: NSObject, NSWindowDelegate {
    let id: String
    let terminal: LocalTerminalSession
    private let window: NSWindow
    private let onClose: (String) -> Void
    private var closed = false

    init(id: String, title: String, directory: URL, input: TerminalInput, controller: TerminalController, onClose: @escaping (String) -> Void, onInputProblem: @escaping (String) -> Void) {
        self.id = id
        self.onClose = onClose
        var close: () -> Void = {}
        terminal = LocalTerminalSession(id: id, directory: directory.path, controller: controller, smoke: false,
                                        statusChanged: { _ in }, focused: {}, closeRequested: { close() })
        terminal.initialInput = input
        terminal.initialInputFailed = { command in onInputProblem(L("terminal.initialInputFailed", ["command": command])) }
        terminal.initialInputRefused = { reason in onInputProblem(reason) }
        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 760, height: 480), styleMask: [.titled, .closable, .miniaturizable, .resizable], backing: .buffered, defer: false)
        super.init()
        close = { [weak self] in self?.close() }
        window.title = title
        window.isReleasedWhenClosed = false
        window.delegate = self
        window.contentView = NSHostingView(rootView: SetupTerminalContent(terminal: terminal))
        window.setFrameAutosaveName("MightySetupTerminal")
        window.center()
    }

    func show() {
        window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    func close() {
        guard !closed else { return }
        window.close()
    }

    func windowWillClose(_ notification: Notification) {
        guard !closed else { return }
        closed = true
        terminal.dispose()
        onClose(id)
    }
}

private struct SetupTerminalContent: View {
    @ObservedObject var terminal: LocalTerminalSession

    var body: some View {
        VStack(spacing: 0) {
            NativeTerminalHost(terminal: terminal, onMount: {
                DispatchQueue.main.async { [weak terminal] in
                    guard let terminal, !terminal.disposed, let window = terminal.view.window else { return }
                    window.makeFirstResponder(terminal.view)
                }
            })
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            if let failure = terminal.failure {
                Text(failure).font(.ui(size: 11)).foregroundStyle(.secondary)
                    .padding(DesignMetrics.Spacing.md).frame(maxWidth: .infinity, alignment: .leading).background(Palette.subtle)
            }
        }
        .frame(minWidth: 520, minHeight: 300)
    }
}
