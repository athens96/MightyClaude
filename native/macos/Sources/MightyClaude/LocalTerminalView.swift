import AppKit
import GhosttyTerminal
import MightyCore
import SwiftUI

struct LocalTerminalPane: View {
    @EnvironmentObject private var store: AppStore
    let session: RunSession

    var body: some View {
        Group {
            if let terminal = store.localTerminals[session.id] {
                LocalTerminalContent(terminal: terminal, restart: { Task { await store.restartTerminal(session.id) } })
            } else if let failure = store.terminalErrors[session.id] {
                VStack(spacing: 12) {
                    Text(failure).font(.system(size: 12)).foregroundStyle(.secondary).multilineTextAlignment(.center)
                    Button("다시 시작") { Task { await store.ensureLocalTerminal(session.id) } }
                }.padding(24).frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                ProgressView("터미널 시작 중…").controlSize(.small).frame(maxWidth: .infinity, maxHeight: .infinity)
            }
        }
        .task { await store.ensureLocalTerminal(session.id) }
    }
}

private struct LocalTerminalContent: View {
    @EnvironmentObject private var store: AppStore
    @ObservedObject var terminal: LocalTerminalSession
    let restart: () -> Void

    var body: some View {
        VStack(spacing: 0) {
            NativeTerminalHost(terminal: terminal)
                .id(ObjectIdentifier(terminal))
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            if terminal.exited || terminal.failure != nil {
                HStack(spacing: 8) {
                    Text(terminal.failure ?? "셸이 종료되었습니다.").font(.system(size: 11)).foregroundStyle(.secondary)
                    Spacer(minLength: 4)
                    Button("다시 시작", action: restart).controlSize(.small)
                }.padding(10).background(Palette.subtle)
            }
            HStack(spacing: 7) {
                Text("Ghostty").fontWeight(.medium)
                Text("·")
                Text(terminal.title.isEmpty ? "zsh" : terminal.title).lineLimit(1)
                Spacer(minLength: 4)
                Text(terminal.workingDirectory).lineLimit(1).truncationMode(.middle).help(terminal.workingDirectory)
                if let grid = terminal.grid { Text("\(grid.columns)×\(grid.rows)").monospacedDigit().foregroundStyle(.tertiary) }
            }
            .font(.system(size: 9)).foregroundStyle(.secondary).padding(.horizontal, 10).padding(.vertical, 6)
            .background(Palette.subtle)
        }
        .onAppear { focusIfActive() }
        .onChange(of: terminal.ready) { _, ready in if ready { focusIfActive() } }
        .onChange(of: store.snapshot.activeSessionId) { _, _ in focusIfActive() }
    }

    private func focusIfActive() {
        guard store.snapshot.activeSessionId == terminal.id, !store.hasModal else { return }
        DispatchQueue.main.async { [weak terminal, weak store] in
            guard let terminal, !terminal.disposed, let store, store.snapshot.activeSessionId == terminal.id, !store.hasModal else { return }
            _ = terminal.view.acquireProgrammaticFocus()
        }
    }
}

private struct NativeTerminalHost: NSViewRepresentable {
    let terminal: LocalTerminalSession

    func makeNSView(context: Context) -> TerminalContainer {
        let host = TerminalContainer()
        host.claim(terminal)
        return host
    }
    func updateNSView(_ host: TerminalContainer, context: Context) { host.attach(terminal) }
    static func dismantleNSView(_ host: TerminalContainer, coordinator: ()) { host.detach() }

    final class TerminalContainer: NSView {
        private weak var terminal: LocalTerminalSession?
        private var generation: UInt64?
        private var retired = false

        func claim(_ next: LocalTerminalSession) {
            guard !retired, terminal == nil, generation == nil, !next.disposed else { return }
            terminal = next
            generation = next.claimPresentation(self)
            attach(next)
        }

        func attach(_ next: LocalTerminalSession) {
            // Only makeNSView claims a lease. An old updateNSView must not
            // steal the cached view back from a newly created split/tab host.
            guard !retired, terminal === next, let generation,
                  next.ownsPresentation(self, generation: generation) else { return }
            if next.view.superview !== self {
                next.view.removeFromSuperview()
                next.view.frame = bounds
                next.view.autoresizingMask = [.width, .height]
                addSubview(next.view)
            }
            next.mounted()
        }
        func detach() {
            retired = true
            if let terminal, let generation, terminal.ownsPresentation(self, generation: generation) {
                if terminal.view.superview === self {
                    terminal.view.setSurfaceVisible(false)
                    terminal.view.removeFromSuperview()
                }
                terminal.releasePresentation(self, generation: generation)
            }
            terminal = nil
            generation = nil
        }
        override func layout() {
            super.layout()
            if let terminal, let generation,
               terminal.ownsPresentation(self, generation: generation), terminal.view.superview === self {
                terminal.view.frame = bounds
            }
        }
    }
}

struct LegacyTerminalHistory: View {
    @Environment(\.dismiss) private var dismiss
    let session: RunSession

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack { Text("이전 명령 실행 기록").font(.headline); Spacer(); Button("닫기") { dismiss() }.keyboardShortcut(.cancelAction) }
            Text("명령 실행 창에 저장된 기록입니다. 대화형 터미널의 현재 내용은 터미널에서 선택해 복사할 수 있습니다.").font(.system(size: 12)).foregroundStyle(.secondary)
            ScrollView {
                Text(session.logs.isEmpty ? "저장된 이전 기록이 없습니다." : session.logs.map { "[\($0.kind)] \($0.text)" }.joined(separator: "\n\n"))
                    .font(.system(size: 11, design: .monospaced)).textSelection(.enabled).frame(maxWidth: .infinity, alignment: .leading).padding(12)
            }.background(Palette.subtle, in: RoundedRectangle(cornerRadius: 8))
        }.padding(20).frame(width: 630, height: 450)
    }
}
