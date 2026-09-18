import AppKit
import Combine
import GhosttyTerminal

/// Owns the actual AppKit view, not only its SwiftUI presentation. Detaching a
/// pane keeps the PTY, shell state, and scrollback alive until explicit disposal.
@MainActor
final class LocalTerminalSession: NSObject, ObservableObject, TerminalSurfaceTitleDelegate, TerminalSurfacePwdDelegate, TerminalSurfaceFocusDelegate, TerminalSurfaceGridResizeDelegate, TerminalSurfaceCloseDelegate, TerminalSurfaceLifecycleDelegate, TerminalSurfaceClipboardConfirmationDelegate {
    let id: String
    let view: AppTerminalView
    let controller: TerminalController
    @Published private(set) var title = "zsh"
    @Published private(set) var workingDirectory: String
    @Published private(set) var grid: TerminalGridMetrics?
    @Published private(set) var ready = false
    @Published private(set) var exited = false
    @Published private(set) var failure: String?
    private(set) var disposed = false
    private(set) weak var surface: TerminalSurface?
    private weak var presentationHost: NSView?
    private var presentationGeneration: UInt64 = 0
    private var startCheck: Task<Void, Never>?
    private let statusChanged: (String) -> Void
    private let focused: () -> Void
    private let closeRequested: () -> Void
    /// Run in the shell once, after the surface attaches.
    var initialInput: String?
    var initialInputFailed: ((String) -> Void)?

    init(id: String, directory: String, controller: TerminalController, smoke: Bool, statusChanged: @escaping (String) -> Void, focused: @escaping () -> Void, closeRequested: @escaping () -> Void) {
        self.id = id
        workingDirectory = directory
        self.controller = controller
        self.statusChanged = statusChanged
        self.focused = focused
        self.closeRequested = closeRequested
        view = AppTerminalView(frame: .zero)
        super.init()
        var environment: [String: String] = ["MIGHTYCLAUDE_TERMINAL_ID": id]
        let home = FileManager.default.homeDirectoryForCurrentUser.path
        let paths = (ProcessInfo.processInfo.environment["PATH"] ?? "").split(separator: ":").map(String.init)
            + [home + "/.local/bin", home + "/.npm-global/bin", "/opt/homebrew/bin", "/usr/local/bin", "/usr/bin", "/bin", "/usr/sbin", "/sbin"]
        var seen = Set<String>()
        environment["PATH"] = paths.filter { !$0.isEmpty && seen.insert($0).inserted }.joined(separator: ":")
        if smoke { environment["HISTFILE"] = "/dev/null" }
        // A per-surface command forces Ghostty's wait-after-command mode.
        // The shared controller supplies the shell command instead, so exit
        // reaches the close callback immediately without another key press.
        view.configuration = TerminalSurfaceOptions(backend: .exec, fontSize: 12, workingDirectory: directory, envVars: environment, waitAfterCommand: false, resizeThrottleMilliseconds: 25)
        view.delegate = self
        view.controller = controller
        view.setAccessibilityLabel("대화형 터미널")
        view.setAccessibilityIdentifier("terminal-\(id)")
    }

    func mounted() {
        guard !disposed else { return }
        view.setSurfaceVisible(true)
        if !ready, startCheck == nil {
            startCheck = Task { [weak self] in
                try? await Task.sleep(for: .seconds(8))
                guard !Task.isCancelled, let self, !self.disposed, self.surface == nil else { return }
                self.failure = self.controller.lastConfigurationIssue ?? "터미널을 시작하지 못했습니다. 다시 시작하세요."
                self.statusChanged("error")
            }
        }
    }

    /// New SwiftUI representations supersede earlier ones synchronously. An
    /// outgoing representation may still receive updates before dismantling.
    func claimPresentation(_ host: NSView) -> UInt64 {
        presentationGeneration &+= 1
        presentationHost = host
        return presentationGeneration
    }

    func ownsPresentation(_ host: NSView, generation: UInt64) -> Bool {
        !disposed && presentationHost === host && presentationGeneration == generation
    }

    func releasePresentation(_ host: NSView, generation: UInt64) {
        guard ownsPresentation(host, generation: generation) else { return }
        presentationHost = nil
        presentationGeneration &+= 1
    }

    func dispose() {
        guard !disposed else { return }
        disposed = true
        presentationHost = nil
        presentationGeneration &+= 1
        startCheck?.cancel()
        startCheck = nil
        view.setSurfaceVisible(false)
        view.delegate = nil
        // Setting controller to nil tears down the existing native surface even
        // when its view is detached or has zero size. ARC alone is insufficient.
        view.controller = nil
        view.removeFromSuperview()
        surface = nil
        ready = false
    }

    private func publish(_ action: @escaping (LocalTerminalSession) -> Void) {
        DispatchQueue.main.async { [weak self] in
            guard let self, !self.disposed else { return }
            action(self)
        }
    }

    func terminalDidAttachSurface(_ surface: TerminalSurface) {
        self.surface = surface
        publish { $0.ready = true; $0.failure = nil; $0.statusChanged("running") }
        // A command the app wants run once the shell is up (CLI sign-in).
        // Text goes in as a paste, so Enter is a separate key press.
        if initialInput != nil { typeInitialInput(after: 0.9, attempt: 1) }
    }
    private func typeInitialInput(after delay: TimeInterval, attempt: Int) {
        DispatchQueue.main.asyncAfter(deadline: .now() + delay) { [weak self] in
            guard let self, !self.disposed, let input = self.initialInput else { return }
            if self.view.paste(text: input), self.view.sendKey(.enter) { self.initialInput = nil }
            else if attempt < 3 { self.typeInitialInput(after: 1, attempt: attempt + 1) }
            else { self.initialInput = nil; self.initialInputFailed?(input) }
        }
    }
    func terminalDidDetachSurface() { surface = nil }
    func terminalDidChangeTitle(_ title: String) { publish { $0.title = String(title.prefix(300)) } }
    func terminalDidChangeWorkingDirectory(_ path: String) { publish { $0.workingDirectory = String(path.prefix(4096)) } }
    func terminalDidResize(_ size: TerminalGridMetrics) { publish { $0.grid = size } }
    func terminalDidChangeFocus(_ focused: Bool) { if focused { publish { $0.focused() } } }
    func terminalDidClose(processAlive: Bool) {
        publish {
            if processAlive { $0.closeRequested() }
            else { $0.exited = true; $0.statusChanged("completed") }
        }
    }
    func terminalDidRequestClipboardConfirmation(_ request: TerminalClipboardConfirmationRequest) {
        request.respond(allow: request.kind == .paste)
    }

    /// Used by the isolated diagnostic only; selection reads do not touch the
    /// system pasteboard. Normal terminal selection/copy stays with Ghostty.
    func diagnosticText() -> String {
        guard view.performBindingAction("select_all") else { return "" }
        return surface?.readSelection() ?? ""
    }
}
