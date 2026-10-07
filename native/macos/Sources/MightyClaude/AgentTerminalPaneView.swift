import AppKit
import GhosttyTerminal
import MightyCore
import SwiftUI

/// The Ghostty view of one agent pane's ``PTYAgentTerminalPane``. The surface
/// only renders: it is fed the pane's combined output stream, and what the
/// user types or pastes goes back to the pane, which picks the command to
/// deliver it to. Lives until quit, so a closed terminal pane reopens with its
/// scrollback while the processes keep running.
@MainActor
final class AgentTerminalHost {
    let pane: PTYAgentTerminalPane
    let terminal: LocalTerminalSession
    private let subscription: UUID

    init(pane: PTYAgentTerminalPane, sessionId: String, controller: TerminalController, focused: @escaping () -> Void, closeRequested: @escaping () -> Void) {
        self.pane = pane
        let backend = InMemoryTerminalSession(
            write: { [weak pane] data in pane?.sendUserInput(String(decoding: data, as: UTF8.self)) },
            resize: { [weak pane] viewport in pane?.resize(columns: viewport.columns, rows: viewport.rows) }
        )
        terminal = LocalTerminalSession(id: sessionId, directory: pane.workingDirectory.path, controller: controller, smoke: false, backend: .inMemory(backend),
                                        statusChanged: { _ in }, focused: focused, closeRequested: closeRequested)
        subscription = pane.subscribe { backend.receive($0) }
    }

    func dispose() {
        pane.unsubscribe(subscription)
        terminal.dispose()
    }
}

struct AgentTerminalPaneView: View {
    @EnvironmentObject private var store: AppStore
    let session: RunSession
    private var active: Bool { store.snapshot.activeSessionId == session.id }

    var body: some View {
        Group {
            if let owner = session.ownerSessionId, let host = store.agentTerminals[owner] {
                AgentTerminalContent(terminal: host.terminal)
            } else { Color.clear }
        }
        .background(Palette.panel, in: RoundedRectangle(cornerRadius: 11))
        .overlay { RoundedRectangle(cornerRadius: 11).stroke(active ? Palette.accent.opacity(0.58) : Palette.border, lineWidth: 1).allowsHitTesting(false) }
        .clipShape(RoundedRectangle(cornerRadius: 11))
        .accessibilityElement(children: .contain)
        .accessibilityLabel(session.title)
    }
}

private struct AgentTerminalContent: View {
    @EnvironmentObject private var store: AppStore
    @ObservedObject var terminal: LocalTerminalSession

    var body: some View {
        VStack(spacing: 0) {
            NativeTerminalHost(terminal: terminal, onMount: { focusIfActive() })
                .id(ObjectIdentifier(terminal))
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            if let failure = terminal.failure {
                Text(failure).font(.system(size: 11)).foregroundStyle(.secondary).padding(DesignMetrics.Spacing.md).frame(maxWidth: .infinity, alignment: .leading).background(Palette.subtle)
            }
            HStack(spacing: DesignMetrics.Spacing.sm) {
                Text(L("agentTerminal.terminalPane.title")).fontWeight(.medium)
                Spacer(minLength: 4)
                Text(terminal.workingDirectory).lineLimit(1).truncationMode(.middle).help(terminal.workingDirectory)
                if let grid = terminal.grid { Text("\(grid.columns)×\(grid.rows)").monospacedDigit().foregroundStyle(.tertiary) }
            }
            .font(.system(size: 9)).foregroundStyle(.secondary).padding(.horizontal, DesignMetrics.Spacing.md).padding(.vertical, DesignMetrics.Spacing.sm)
            .background(Palette.subtle)
        }
        .onAppear { focusIfActive() }
        .onChange(of: terminal.ready) { _, ready in if ready { focusIfActive() } }
        .onChange(of: store.snapshot.activeSessionId) { _, _ in focusIfActive(allowReplacingResponder: true) }
    }

    private func focusIfActive(allowReplacingResponder: Bool = false) {
        guard store.snapshot.activeSessionId == terminal.id, !store.hasModal,
              let request = TerminalFocusRequest(view: terminal.view, allowReplacingResponder: allowReplacingResponder) else { return }
        DispatchQueue.main.async { [weak terminal, weak store] in
            guard let terminal, !terminal.disposed, let store, store.snapshot.activeSessionId == terminal.id, !store.hasModal else { return }
            request.perform(appActive: NSApp.isActive, keyWindow: NSApp.keyWindow, modalWindow: NSApp.modalWindow)
        }
    }
}
