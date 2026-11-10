import AppKit
import Carbon
import Foundation
import MightyCore

/// Records what the text-input system does around each key in the composer
/// and recognises the broken state (Korean typed as separate jamo). When it
/// triggers, a snapshot of app/window/input-context/secure-input state and
/// the recent call log is written to `<data>/diagnostics/`, and the pane
/// offers a reconnection attempt.
@MainActor
final class InputMethodMonitor {
    static let shared = InputMethodMonitor()
    struct Event { let time: TimeInterval; let text: String }
    struct Problem: Equatable {
        enum Reason { case uncombinedJamo, unavailableSession }
        let detectedAt: Date
        var file: URL?
        var recoveryAttempts = 0
        var reason: Reason = .uncombinedJamo
        var recoveryState: InputSessionRecoveryCoordinator.State = .idle
    }

    private(set) var events: [Event] = []
    private var detector = InputMethodSymptom.Detector()
    private var composedThisKey = false
    private var keyDepth = 0
    private var unhealthyEditor: ObjectIdentifier?
    private var unhealthyKeyCount = 0
    private var lastUnhealthyKey: TimeInterval = 0
    private var lastSessionSnapshot = Date.distantPast
    var dataDirectory: URL?
    var onProblem: ((Problem) -> Void)?
    var onRecovered: (() -> Void)?
    private(set) var problem: Problem?
    private var problemRevision: UInt64 = 0
    private let launchedAt = Date()
    private var lifecycleEvents: [[String: Any]] = []
    private var lifecycleObservers: [NSObjectProtocol] = []
    private let recoveryCoordinator: InputSessionRecoveryCoordinator
    private let contextIsCurrent: @MainActor (NSTextView) -> Bool

    init(recoveryCoordinator: InputSessionRecoveryCoordinator? = nil, contextIsCurrent: @escaping @MainActor (NSTextView) -> Bool = {
        $0.inputContext != nil && $0.inputContext === NSTextInputContext.current
    }) {
        self.recoveryCoordinator = recoveryCoordinator ?? .shared
        self.contextIsCurrent = contextIsCurrent
        for name in [NSApplication.didBecomeActiveNotification, NSApplication.didResignActiveNotification,
                     NSWindow.didBecomeKeyNotification, NSWindow.didResignKeyNotification,
                     NSTextInputContext.keyboardSelectionDidChangeNotification] {
            lifecycleObservers.append(NotificationCenter.default.addObserver(forName: name, object: nil, queue: .main) { [weak self] note in
                MainActor.assumeIsolated { self?.recordLifecycle(name: name.rawValue, window: note.object as? NSWindow) }
            })
        }
        recordLifecycle(name: "monitorStarted", window: nil)
    }

    deinit { lifecycleObservers.forEach { NotificationCenter.default.removeObserver($0) } }

    private func recordLifecycle(name: String, window: NSWindow?) {
        var event: [String: Any] = ["time": Date().timeIntervalSince1970, "event": name,
                                  "appIsActive": NSApp.isActive, "keyWindowNumber": NSApp.keyWindow?.windowNumber ?? 0,
                                  "frontmostIsOwnApplication": NSWorkspace.shared.frontmostApplication?.processIdentifier == ProcessInfo.processInfo.processIdentifier,
                                  "currentInputSource": Self.currentInputSourceID() ?? "-"]
        if let window {
            event["windowNumber"] = window.windowNumber
            event["windowClass"] = String(describing: type(of: window))
            event["windowIsKey"] = window.isKeyWindow
            event["windowIsVisible"] = window.isVisible
        }
        lifecycleEvents.append(event)
        if lifecycleEvents.count > 60 { lifecycleEvents.removeFirst(lifecycleEvents.count - 60) }
    }

    func record(_ text: String) {
        events.append(Event(time: Date().timeIntervalSince1970, text: text))
        if events.count > 400 { events.removeFirst(events.count - 400) }
    }

    func keyBegan(_ event: NSEvent, in editor: NSTextView? = nil) {
        keyDepth += 1
        if keyDepth == 1 { composedThisKey = false }
        record("keyDown code=\(event.keyCode) chars=\(event.characters.map { String($0.unicodeScalars.map { String(format: "U+%04X", $0.value) }.joined(separator: " ")) } ?? "-")")
        if let editor { observeSessionHealth(event, in: editor) }
    }
    func keyEnded() { keyDepth = max(0, keyDepth - 1) }

    /// Observe the app/window failure even if the input method inserts ASCII or
    /// nothing at all. This never changes focus, the input source or the draft.
    /// Two direct keys distinguish a continuing failure from a focus transition;
    /// a bounded metadata-only snapshot is written after the current key returns.
    private func observeSessionHealth(_ event: NSEvent, in editor: NSTextView) {
        guard event.type == .keyDown, !event.modifierFlags.contains(.command),
              !event.modifierFlags.contains(.control),
              let window = editor.window, event.window === window,
              window.firstResponder === editor, window.isVisible,
              !editor.isHiddenOrHasHiddenAncestor, editor.isEditable else { return }
        recoveryCoordinator.rememberFocusedEditor(editor)
        let unhealthy = !NSApp.isActive || !window.isKeyWindow
            || !contextIsCurrent(editor)
        guard unhealthy else { unhealthyEditor = nil; unhealthyKeyCount = 0; return }
        let now = Date()
        let identity = ObjectIdentifier(editor)
        if unhealthyEditor != identity || now.timeIntervalSince1970 - lastUnhealthyKey > 10 { unhealthyKeyCount = 0 }
        unhealthyEditor = identity; unhealthyKeyCount += 1; lastUnhealthyKey = now.timeIntervalSince1970
        guard unhealthyKeyCount >= 2 else { return }
        if problem == nil || problem?.recoveryState == .reconnected {
            problem = Problem(detectedAt: now, file: nil, recoveryAttempts: problem?.recoveryAttempts ?? 0, reason: .unavailableSession)
            publishProblemAfterInput()
        }
        guard now.timeIntervalSince(lastSessionSnapshot) > 60,
              let directory = dataDirectory?.appendingPathComponent("diagnostics", isDirectory: true) else { return }
        lastSessionSnapshot = now
        var info = snapshot(editor: editor)
        info.removeValue(forKey: "editorString")
        info.removeValue(forKey: "recentEvents")
        info.removeValue(forKey: "keyWindow")
        info.removeValue(forKey: "mainWindow")
        info["reason"] = "input session unavailable during repeated direct keyboard input"
        info["consecutiveUnhealthyKeys"] = unhealthyKeyCount
        info["automaticRecovery"] = false
        let detectedAt = problem?.detectedAt
        DispatchQueue.main.async { [weak self, info] in
            let file = Self.persistSnapshot(info, in: directory)
            if let self, self.problem?.detectedAt == detectedAt {
                self.problem?.file = file
                self.publishProblemAfterInput()
            }
        }
    }

    func noteMarkedText(_ string: Any, selectedRange: NSRange, in editor: NSTextView) {
        composedThisKey = true
        let text = (string as? String) ?? (string as? NSAttributedString)?.string ?? "?"
        record("setMarkedText \(Self.codes(text)) sel=\(selectedRange) marked=\(editor.markedRange())")
    }
    func noteUnmark(in editor: NSTextView) { record("unmarkText hadMarked=\(editor.hasMarkedText())") }

    /// Reports repeated uncombined jamo. Observation never rewrites input.
    @discardableResult
    func noteInsert(_ string: Any, replacementRange: NSRange, source: String?, in editor: NSTextView) -> Bool {
        let text = (string as? String) ?? (string as? NSAttributedString)?.string ?? ""
        let replaced = replacementRange.location == NSNotFound ? "none" : NSStringFromRange(replacementRange)
        // Distinguish conjoining jamo from compatibility jamo in diagnostics.
        let conjoining = text.unicodeScalars.count == 1 && text.unicodeScalars.first.map { (0x1100...0x11FF).contains($0.value) } == true
        record("insertText \(Self.codes(text)) replace=\(replaced) marked=\(composedThisKey) source=\(source ?? "-")" + (conjoining ? " conjoining=true" : ""))
        let koreanSource = InputMethodSymptom.isKoreanInputSource(source)
        // A warning that outlives the fault is its own bug: once a key press
        // composes again, take the notice down without being asked.
        if problem != nil, keyDepth > 0,
           InputMethodSymptom.provesComposition(text, hasReplacementRange: replacementRange.location != NSNotFound, koreanSource: koreanSource) {
            record("recovered: the input method is composing again")
            clearProblem()
            publishProblemAfterInput()
            return false
        }
        let caret = min(editor.selectedRange().location, (editor.string as NSString).length)
        let before = (editor.string as NSString).substring(with: NSRange(location: max(0, caret - 2), length: min(2, caret)))
        guard detector.observeInsert(textBeforeCaret: before, koreanSource: koreanSource, at: Date().timeIntervalSince1970) else { return false }
        guard problem == nil || problem?.recoveryState == .reconnected else { return true }
        let file = writeDiagnostics(editor: editor, reason: "consonant and vowel jamo left uncombined twice (input method stopped replacing)")
        let raised = Problem(detectedAt: Date(), file: file)
        problem = raised
        publishProblemAfterInput()
        return true
    }

    /// UI notices follow the same boundary as draft publication. A newer
    /// symptom, recovery or manual dismissal supersedes any queued notice.
    private func publishProblemAfterInput() {
        problemRevision &+= 1
        let revision = problemRevision
        DispatchQueue.main.async { [weak self] in
            guard let self, self.problemRevision == revision else { return }
            if let problem = self.problem { self.onProblem?(problem) }
            else { self.onRecovered?() }
        }
    }

    func noteFirstRect(_ rect: NSRect, range: NSRange) { record("firstRect range=\(range) rect=\(Int(rect.origin.x)),\(Int(rect.origin.y)) \(Int(rect.width))x\(Int(rect.height))") }

    /// Everything that could explain a dead composition session.
    func snapshot(editor: NSTextView?) -> [String: Any] {
        var info: [String: Any] = [
            "time": ISO8601DateFormatter().string(from: Date()),
            "bundlePath": Bundle.main.bundlePath,
            "version": Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "?",
            "processStart": ISO8601DateFormatter().string(from: launchedAt),
            "appIsActive": NSApp.isActive,
            "activationPolicy": NSApp.activationPolicy().rawValue,
            "appIsHidden": NSApp.isHidden,
            "frontmostIsOwnApplication": NSWorkspace.shared.frontmostApplication?.processIdentifier == ProcessInfo.processInfo.processIdentifier,
            "frontmostProcessIdentifier": NSWorkspace.shared.frontmostApplication?.processIdentifier ?? 0,
            "keyWindowNumber": NSApp.keyWindow?.windowNumber ?? 0,
            "mainWindowNumber": NSApp.mainWindow?.windowNumber ?? 0,
            "keyWindow": NSApp.keyWindow?.title ?? "-",
            "mainWindow": NSApp.mainWindow?.title ?? "-",
            "firstResponder": NSApp.keyWindow?.firstResponder.map { String(describing: type(of: $0)) } ?? "-",
            "secureEventInput": IsSecureEventInputEnabled(),
            "currentInputSource": Self.currentInputSourceID() ?? "-",
            "keyboardLayoutSource": Self.currentLayoutSourceID() ?? "-",
            "textInputContextCurrent": NSTextInputContext.current.map { String(describing: ObjectIdentifier($0)) } ?? "-",
            "textInputContextClientClass": NSTextInputContext.current.map { String(describing: type(of: $0.client)) } ?? "-",
            "textInputContextSource": NSTextInputContext.current?.selectedKeyboardInputSource ?? "-",
        ]
        if let editor {
            info["editorHasMarkedText"] = editor.hasMarkedText()
            info["editorMarkedRange"] = NSStringFromRange(editor.markedRange())
            info["editorSelectedRange"] = NSStringFromRange(editor.selectedRange())
            info["editorInWindow"] = editor.window != nil
            info["editorIsFirstResponder"] = editor.window?.firstResponder === editor
            info["editorContextIsCurrent"] = editor.inputContext != nil && editor.inputContext === NSTextInputContext.current
            info["editorContextSource"] = editor.inputContext?.selectedKeyboardInputSource ?? "-"
            info["editorContextAccepts"] = editor.inputContext?.acceptsGlyphInfo ?? false
            info["editorString"] = String(editor.string.suffix(40))
            if let window = editor.window {
                info["editorWindowNumber"] = window.windowNumber
                info["editorWindowClass"] = String(describing: type(of: window))
                info["editorWindowIsKey"] = window.isKeyWindow
                info["editorWindowIsMain"] = window.isMainWindow
                info["editorWindowIsVisible"] = window.isVisible
                info["editorWindowIsMiniaturized"] = window.isMiniaturized
                info["editorWindowCanBecomeKey"] = window.canBecomeKey
                info["editorWindowStyleMask"] = window.styleMask.rawValue
                info["editorWindowMatchesAppKeyWindow"] = NSApp.keyWindow === window
            }
        }
        info["recentLifecycleEvents"] = lifecycleEvents
        info["recentEvents"] = events.suffix(120).map { String(format: "%.3f ", $0.time) + $0.text }
        return info
    }

    @discardableResult
    func writeDiagnostics(editor: NSTextView?, reason: String) -> URL? {
        guard let directory = dataDirectory?.appendingPathComponent("diagnostics", isDirectory: true) else { return nil }
        var info = snapshot(editor: editor); info["reason"] = reason
        return Self.persistSnapshot(info, in: directory)
    }

    private static func persistSnapshot(_ info: [String: Any], in directory: URL) -> URL? {
        let stamp = ISO8601DateFormatter().string(from: Date()).replacingOccurrences(of: ":", with: "")
        let file = directory.appendingPathComponent("ime-\(stamp)-\(UUID().uuidString.prefix(8)).json")
        guard JSONSerialization.isValidJSONObject(info), let data = try? JSONSerialization.data(withJSONObject: info, options: [.prettyPrinted, .sortedKeys]) else { return nil }
        do {
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
            try data.write(to: file, options: .atomic)
            try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: file.path)
            return file
        } catch { return nil }
    }

    /// Commit visible preedit before reconnecting so recovery never removes the
    /// last syllable. Kept separate so diagnostics can verify it without changing
    /// the user's focus or keyboard source.
    func commitBeforeRecovery(in editor: NSTextView) {
        if let composer = editor as? ComposerTextView { composer.prepareForSubmission() }
        else if editor.hasMarkedText() {
            editor.unmarkText()
            editor.inputContext?.discardMarkedText()
        }
    }

    /// Reconnect through the owning window and first responder. Never toggle
    /// the global input source: a delayed ABC→Korean switch can race a user's
    /// focus/source change and leave the status menu and editor out of sync.
    func attemptRecovery(editor: NSTextView?) {
        record("recovery attempt")
        if problem == nil { problem = Problem(detectedAt: Date(), file: nil, reason: .unavailableSession) }
        problem?.recoveryAttempts += 1
        publishProblemAfterInput()
        let coordinator = recoveryCoordinator
        coordinator.onStateChange = { [weak self] state in
            guard let self, self.problem != nil else { return }
            self.problem?.recoveryState = state
            self.record("recovery state=\(state)")
            if state == .reconnected {
                self.detector.reset()
                self.unhealthyEditor = nil; self.unhealthyKeyCount = 0; self.lastUnhealthyKey = 0
            }
            self.publishProblemAfterInput()
        }
        coordinator.requestRecovery(editor: editor) { [weak self] target in self?.commitBeforeRecovery(in: target) }
    }

    func clearProblem() {
        recoveryCoordinator.cancel(notify: false)
        problem = nil; detector.reset(); problemRevision &+= 1
    }

    static func currentInputSourceID() -> String? {
        guard let source = TISCopyCurrentKeyboardInputSource()?.takeRetainedValue() else { return nil }
        return inputSourceProperty(source, kTISPropertyInputSourceID)
    }
    static func currentLayoutSourceID() -> String? {
        guard let source = TISCopyCurrentKeyboardLayoutInputSource()?.takeRetainedValue() else { return nil }
        return inputSourceProperty(source, kTISPropertyInputSourceID)
    }
    private static func inputSourceProperty(_ source: TISInputSource, _ key: CFString) -> String? {
        guard let pointer = TISGetInputSourceProperty(source, key) else { return nil }
        return Unmanaged<CFString>.fromOpaque(pointer).takeUnretainedValue() as String
    }
    private static func codes(_ text: String) -> String {
        "\"" + text.prefix(8) + "\" " + text.unicodeScalars.prefix(8).map { String(format: "U+%04X", $0.value) }.joined(separator: " ")
    }
}
