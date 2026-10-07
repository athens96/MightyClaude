import AppKit
import Combine
import GhosttyTerminal
import MightyCore

/// Owns the actual AppKit view, not only its SwiftUI presentation. Detaching a
/// pane keeps the PTY, shell state, and scrollback alive until explicit disposal.
@MainActor
final class LocalTerminalSession: NSObject, ObservableObject, TerminalSurfaceTitleDelegate, TerminalSurfacePwdDelegate, TerminalSurfaceFocusDelegate, TerminalSurfaceGridResizeDelegate, TerminalSurfaceCloseDelegate, TerminalSurfaceLifecycleDelegate, TerminalSurfaceClipboardConfirmationDelegate, TerminalSurfaceCommandFinishedDelegate {
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
    private var focusPublicationRevision: UInt64 = 0
    private var startCheck: Task<Void, Never>?
    private let statusChanged: (String) -> Void
    /// The shell `ShellProcessProbe` found for this pane, with its start time so a
    /// reused pid is noticed, and when the next reading is due.
    private(set) var shellProcessId: pid_t?
    /// Where the pane's command writes the shell's pid; nil turns the reading off.
    private let pidFile: URL?
    private var shellStarted: UInt64 = 0
    private var nextActivityProbe = ContinuousClock.now
    private let focused: () -> Void
    private let closeRequested: () -> Void
    /// Typed into the shell once, after the surface attaches. Whether Enter
    /// follows is `TerminalInput.autoRun`, and that is the app's own decision:
    /// a string that came from a manifest never runs itself (§1.5).
    var initialInput: TerminalInput?
    var initialInputFailed: ((String) -> Void)?
    /// Told when the policy refused the text outright (a line break in it).
    var initialInputRefused: ((String) -> Void)?

    /// `backend` is `.exec` for a shell Ghostty owns, or `.inMemory` for a view
    /// of PTYs the app owns (an agent's terminal pane).
    init(id: String, directory: String, controller: TerminalController, smoke: Bool, backend: TerminalSessionBackend = .exec, statusChanged: @escaping (String) -> Void, focused: @escaping () -> Void, closeRequested: @escaping () -> Void) {
        self.id = id
        workingDirectory = directory
        self.controller = controller
        self.statusChanged = statusChanged
        self.focused = focused
        self.closeRequested = closeRequested
        pidFile = ShellProcessProbe.pidFile(terminalId: id)
        let terminalView = HostTerminalView(frame: .zero)
        view = terminalView
        super.init()
        terminalView.readSelectedText = { [weak self] in
            guard let surface = self?.surface, surface.hasSelection() else { return nil }
            return surface.readSelection()
        }
        var environment: [String: String] = ["MIGHTYCLAUDE_TERMINAL_ID": id]
        let home = FileManager.default.homeDirectoryForCurrentUser.path
        let paths = (ProcessInfo.processInfo.environment["PATH"] ?? "").split(separator: ":").map(String.init)
            + [home + "/.local/bin", home + "/.npm-global/bin", "/opt/homebrew/bin", "/usr/local/bin", "/usr/bin", "/bin", "/usr/sbin", "/sbin"]
        var seen = Set<String>()
        environment["PATH"] = paths.filter { !$0.isEmpty && seen.insert($0).inserted }.joined(separator: ":")
        if smoke { environment["HISTFILE"] = "/dev/null" }
        // The pane's command (`ShellProcessProbe.shellCommand`) writes the shell's pid
        // here and removes the variable before the shell starts.
        if let pidFile {
            try? FileManager.default.removeItem(at: pidFile)
            environment[ShellProcessProbe.pidFileVariable] = pidFile.path
        }
        // A per-surface command forces Ghostty's wait-after-command mode.
        // The shared controller supplies the shell command instead, so exit
        // reaches the close callback immediately without another key press.
        view.configuration = TerminalSurfaceOptions(backend: backend, fontSize: 12, workingDirectory: directory, envVars: environment, waitAfterCommand: false, resizeThrottleMilliseconds: 25)
        view.delegate = self
        view.controller = controller
        view.setAccessibilityLabel(L("terminal.accessibility"))
        view.setAccessibilityIdentifier("terminal-\(id)")
    }

    func mounted() {
        guard !disposed else { return }
        view.setSurfaceVisible(true)
        if !ready, startCheck == nil {
            startCheck = Task { [weak self] in
                try? await Task.sleep(for: .seconds(8))
                guard !Task.isCancelled, let self, !self.disposed, self.surface == nil else { return }
                self.failure = self.controller.lastConfigurationIssue ?? L("terminal.startFailed")
                self.statusChanged("error")
            }
        }
    }

    /// New SwiftUI representations supersede earlier ones synchronously. An
    /// outgoing representation may still receive updates before dismantling.
    func claimPresentation(_ host: NSView) -> UInt64 {
        focusPublicationRevision &+= 1
        presentationGeneration &+= 1
        presentationHost = host
        return presentationGeneration
    }

    func ownsPresentation(_ host: NSView, generation: UInt64) -> Bool {
        !disposed && presentationHost === host && presentationGeneration == generation
    }

    func releasePresentation(_ host: NSView, generation: UInt64) {
        guard ownsPresentation(host, generation: generation) else { return }
        focusPublicationRevision &+= 1
        presentationHost = nil
        presentationGeneration &+= 1
    }

    func dispose() {
        guard !disposed else { return }
        disposed = true
        focusPublicationRevision &+= 1
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
        if let pidFile { try? FileManager.default.removeItem(at: pidFile) }
    }

    private func publish(_ action: @escaping (LocalTerminalSession) -> Void) {
        DispatchQueue.main.async { [weak self] in
            guard let self, !self.disposed else { return }
            action(self)
        }
    }

    func terminalDidAttachSurface(_ surface: TerminalSurface) {
        self.surface = surface
        publish { $0.ready = true; $0.failure = nil; $0.statusChanged("idle") }
        // Text the app wants in the shell once it is up (a CLI sign-in, a
        // style's install command). The retries and the delay stay here; the
        // paste and the Enter live in the engine's policy (§5.7).
        if initialInput != nil { typeInitialInput(after: 0.9, attempt: 1) }
    }
    private func typeInitialInput(after delay: TimeInterval, attempt: Int) {
        DispatchQueue.main.asyncAfter(deadline: .now() + delay) { [weak self] in
            guard let self, !self.disposed, let input = self.initialInput else { return }
            switch TerminalInputPolicy.apply(input, to: self) {
            case .pasted, .pastedAndRan:
                self.initialInput = nil
            case .refused(let reason):
                self.initialInput = nil
                self.initialInputRefused?(reason)
            case .failed:
                if attempt < 3 { self.typeInitialInput(after: 1, attempt: attempt + 1) }
                else { self.initialInput = nil; self.initialInputFailed?(input.text) }
            }
        }
    }
    func terminalDidDetachSurface() {
        focusPublicationRevision &+= 1
        surface = nil
    }
    func terminalDidChangeTitle(_ title: String) { publish { $0.title = String(title.prefix(300)) } }
    func terminalDidChangeWorkingDirectory(_ path: String) {
        // The shell reports its folder at the prompt, so a command has just ended.
        nextActivityProbe = .now
        publish { $0.workingDirectory = String(path.prefix(4096)) }
    }
    func terminalDidFinishCommand(exitCode: Int?, durationNanos: UInt64) { nextActivityProbe = .now }
    func terminalDidResize(_ size: TerminalGridMetrics) { publish { $0.grid = size } }
    func terminalDidChangeFocus(_ focused: Bool) {
        // Ghostty reports focus synchronously while AppKit is changing the
        // responder. Publishing pane selection waits until the next turn, but
        // a subsequent blur or remount must invalidate that queued selection.
        focusPublicationRevision &+= 1
        guard focused, !disposed, let host = presentationHost,
              view.superview === host, let window = view.window,
              host.window === window else { return }
        let revision = focusPublicationRevision
        let generation = presentationGeneration
        DispatchQueue.main.async { [weak self, weak host, weak window] in
            guard let self, let host, let window,
                  self.focusPublicationRevision == revision,
                  self.ownsPresentation(host, generation: generation),
                  self.view.superview === host, self.view.window === window,
                  host.window === window,
                  !self.view.isHiddenOrHasHiddenAncestor, !self.view.visibleRect.isEmpty,
                  NSApp.isActive, NSApp.keyWindow === window,
                  window.isKeyWindow, window.isVisible, !window.isMiniaturized,
                  NSApp.modalWindow == nil, window.attachedSheet == nil,
                  window.firstResponder === self.view else { return }
            self.focused()
        }
    }
    func terminalDidClose(processAlive: Bool) {
        publish {
            if processAlive { $0.closeRequested() }
            else { $0.exited = true; $0.statusChanged("completed") }
        }
    }
    /// Sets the pane `running` while the shell runs a command and `idle` at its
    /// prompt, read about every 500 ms (sooner after a command ends). A pane that
    /// has exited or failed keeps that status, and a reading that cannot be
    /// taken leaves the status as it is.
    func refreshActivity(currentStatus: String) {
        let now = ContinuousClock.now
        guard ready, !exited, failure == nil, !disposed, !["completed", "error"].contains(currentStatus), now >= nextActivityProbe else { return }
        nextActivityProbe = now + .milliseconds(500)
        if let status = ShellActivity.nextStatus(current: currentStatus, busy: shellBusy()) { statusChanged(status) }
    }

    private func shellBusy() -> Bool? {
        if let pid = shellProcessId {
            if let groups = ShellProcessProbe.groups(pid: pid), groups.started == shellStarted {
                return groups.tpgid > 0 ? ShellActivity.isBusy(shellGroup: groups.pgid, foregroundGroup: groups.tpgid) : nil
            }
            // Gone, or the pid now belongs to another process: find the shell again.
            shellProcessId = nil
        }
        guard let pidFile, let pid = ShellProcessProbe.findShellPid(pidFile: pidFile), let groups = ShellProcessProbe.groups(pid: pid) else { return nil }
        shellProcessId = pid
        shellStarted = groups.started
        return groups.tpgid > 0 ? ShellActivity.isBusy(shellGroup: groups.pgid, foregroundGroup: groups.tpgid) : nil
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

/// The thin adapter of §5.7: the policy owns the decision, this owns the view.
/// Both calls already arrive on the main queue, from `typeInitialInput`.
extension LocalTerminalSession: TerminalPasteSink {
    nonisolated func paste(text: String) -> Bool { MainActor.assumeIsolated { view.paste(text: text) } }
    nonisolated func sendEnter() -> Bool { MainActor.assumeIsolated { view.sendKey(.enter) } }
}
