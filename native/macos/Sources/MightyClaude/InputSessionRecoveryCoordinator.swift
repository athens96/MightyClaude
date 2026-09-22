import AppKit

@MainActor
protocol InputSessionRecoveryInputTransaction: AnyObject {
    var isUpdatingInput: Bool { get }
}

/// A user-requested reconnection belongs to one visible editor and its window.
/// Activation is asynchronous: no responder or composition changes happen until
/// AppKit actually reports that application and window as active.
@MainActor
final class InputSessionRecoveryCoordinator {
    static let shared = InputSessionRecoveryCoordinator()

    enum Failure: Equatable {
        case noEditableTarget, activationUnavailable, windowUnavailable, inputContextUnavailable, responderRejected
    }
    enum State: Equatable {
        case idle, activating, verifying, reconnected, cancelled, failed(Failure)

        var isPending: Bool { self == .activating || self == .verifying }
        var message: String {
            switch self {
            case .idle: "입력기 다시 연결을 누르면 현재 입력창의 앱·창·입력 연결을 확인합니다."
            case .activating: "앱과 입력창이 실제로 활성화되기를 기다리고 있습니다."
            case .verifying: "입력창을 다시 연결하고 macOS 입력 컨텍스트를 확인하고 있습니다."
            case .reconnected: "앱·창·입력 컨텍스트 연결을 확인했습니다. 한글 입력을 다시 시도해 주세요."
            case .cancelled: "포커스나 실행 창이 바뀌어 재연결을 취소했습니다. 사용할 입력창에서 다시 시도해 주세요."
            case .failed(.noEditableTarget): "현재 보이는 입력창을 찾지 못했습니다. 사용할 입력창을 클릭한 뒤 다시 시도해 주세요."
            case .failed(.activationUnavailable): "macOS가 앱을 활성화하지 않았습니다. 앱을 완전히 종료(⌘Q)한 뒤 다시 열어 주세요."
            case .failed(.windowUnavailable): "입력창의 키 윈도우 연결을 확인하지 못했습니다. 앱을 완전히 종료(⌘Q)한 뒤 다시 열어 주세요."
            case .failed(.inputContextUnavailable): "창은 활성화됐지만 macOS 입력 연결을 확인하지 못했습니다. 앱을 완전히 종료(⌘Q)한 뒤 다시 열어 주세요."
            case .failed(.responderRejected): "창이 입력창의 포커스를 받아들이지 않았습니다. 입력창을 클릭한 뒤 다시 시도해 주세요."
            }
        }
    }

    /// The same coordinator is exercised with a deterministic event clock and
    /// fake application state; diagnostics never activate the user's application.
    @MainActor
    struct Environment {
        var appIsActive: () -> Bool
        var keyWindow: () -> NSWindow?
        var modalWindow: () -> NSWindow?
        var contextIsCurrent: (NSTextView) -> Bool
        var activate: () -> Void
        var makeKey: (NSWindow) -> Void
        var makeFirstResponder: (NSWindow, NSResponder?) -> Bool
        var now: () -> TimeInterval
        var schedule: (TimeInterval, @escaping () -> Void) -> Void
        var ownAppIsFrontmost: () -> Bool = { NSWorkspace.shared.frontmostApplication?.processIdentifier == ProcessInfo.processInfo.processIdentifier }
        var editorIsProcessingInput: (NSTextView) -> Bool = { ($0 as? InputSessionRecoveryInputTransaction)?.isUpdatingInput ?? false }

        static var live: Environment {
            Environment(appIsActive: { NSApp.isActive }, keyWindow: { NSApp.keyWindow }, modalWindow: { NSApp.modalWindow },
                        contextIsCurrent: { $0.inputContext != nil && $0.inputContext === NSTextInputContext.current },
                        activate: { NSApp.activate() }, makeKey: { $0.makeKey() },
                        makeFirstResponder: { $0.makeFirstResponder($1) }, now: { ProcessInfo.processInfo.systemUptime },
                        schedule: { delay, action in DispatchQueue.main.asyncAfter(deadline: .now() + delay) { action() } })
        }
    }

    @MainActor
    private final class Request {
        weak var editor: NSTextView?
        weak var window: NSWindow?
        weak var parent: NSView?
        weak var control: NSControl?
        weak var controlParent: NSView?
        let usesFieldEditor: Bool
        let started: TimeInterval
        let commit: (NSTextView) -> Void
        let automatic: Bool
        var requestedKey = false
        var rebound = false

        init(editor: NSTextView, window: NSWindow, now: TimeInterval, automatic: Bool = false, commit: @escaping (NSTextView) -> Void) {
            self.editor = editor; self.window = window; parent = editor.superview
            usesFieldEditor = editor.isFieldEditor
            control = usesFieldEditor ? editor.delegate as? NSControl : nil
            controlParent = control?.superview
            started = now; self.commit = commit; self.automatic = automatic
        }
    }

    /// A click is intent, not permission to choose an arbitrary editor later.
    /// Its exact hit view must still own the native responder after dispatch.
    @MainActor
    private final class LifecycleIntent {
        weak var target: NSView?
        weak var parent: NSView?
        weak var window: NSWindow?
        weak var priorResponder: NSResponder?
        weak var priorControl: NSControl?
        let hadPriorResponder: Bool
        let explicitClick: Bool
        let started: TimeInterval
        var requestedActivation = false
        var requestedKey = false
        var requestedFocus = false
        init(target: NSView, window: NSWindow, started: TimeInterval, explicitClick: Bool) {
            self.target = target; parent = target.superview; self.window = window; self.started = started
            priorResponder = window.firstResponder; hadPriorResponder = window.firstResponder != nil
            priorControl = (window.firstResponder as? NSTextView).flatMap { $0.isFieldEditor ? $0.delegate as? NSControl : nil }
            self.explicitClick = explicitClick
        }
    }

    private let environment: Environment
    private let observesNotifications: Bool
    private weak var rememberedEditor: NSTextView?
    private weak var rememberedWindow: NSWindow?
    private weak var rememberedControl: NSControl?
    private var request: Request?
    private var observers: [NSObjectProtocol] = []
    private var trackingObservers: [NSObjectProtocol] = []
    private var workspaceObservers: [NSObjectProtocol] = []
    private var mouseMonitor: Any?
    private var lifecycleInputObservers: [NSObjectProtocol] = []
    private var lifecycleIntent: LifecycleIntent?
    private var lifecycleGeneration: UInt64 = 0
    private var generation: UInt64 = 0
    private var rebinding = false
    private var advancing = false
    private let timeout: TimeInterval = 2
    private(set) var state: State = .idle
    var onStateChange: ((State) -> Void)?

    init(environment: Environment? = nil, observesNotifications: Bool = true) {
        self.environment = environment ?? .live; self.observesNotifications = observesNotifications
        if observesNotifications { installTrackingObservers() }
    }

    deinit {
        (observers + trackingObservers + lifecycleInputObservers).forEach { NotificationCenter.default.removeObserver($0) }
        workspaceObservers.forEach { NSWorkspace.shared.notificationCenter.removeObserver($0) }
        if let mouseMonitor { NSEvent.removeMonitor(mouseMonitor) }
    }

    func rememberFocusedEditor(_ editor: NSTextView) {
        guard let window = editor.window, window.firstResponder === editor else { return }
        if let request, !rebinding,
           request.editor !== editor || (request.usesFieldEditor && fieldEditorOwner(editor) !== request.control) {
            finish(.cancelled)
        }
        rememberedEditor = editor; rememberedWindow = window
        rememberedControl = editor.isFieldEditor ? fieldEditorOwner(editor) : nil
    }

    func forgetEditor(_ editor: NSTextView) {
        guard !rebinding else { return }
        retireEditor(editor)
    }

    func retireEditor(_ editor: NSTextView) {
        if lifecycleIntent?.target === editor { cancelAutomaticWork() }
        if request?.editor === editor { finish(.cancelled) }
        if rememberedEditor === editor { rememberedEditor = nil; rememberedWindow = nil; rememberedControl = nil }
    }

    /// The last focused editor is a fallback only while it remains the native
    /// first responder of the same visible window. No hierarchy-wide search or
    /// new focus choice is made on behalf of the user.
    func resolveEditor(preferred: NSTextView? = nil) -> NSTextView? {
        if let preferred { return eligible(preferred) ? preferred : nil }
        if let editor = environment.keyWindow()?.firstResponder as? NSTextView, eligible(editor) { return editor }
        guard let editor = rememberedEditor, editor.window === rememberedWindow, eligible(editor) else { return nil }
        if editor.isFieldEditor, fieldEditorOwner(editor) !== rememberedControl { return nil }
        return editor
    }

    func requestRecovery(editor preferred: NSTextView?, commit: @escaping (NSTextView) -> Void) {
        cancel(notify: false)
        guard let editor = resolveEditor(preferred: preferred), let window = editor.window else {
            transition(.failed(.noEditableTarget)); return
        }
        if let keyWindow = environment.keyWindow(), keyWindow !== window {
            transition(.cancelled); return
        }
        beginRecovery(editor: editor, window: window, automatic: false, commit: commit)
    }

    private func beginRecovery(editor: NSTextView, window: NSWindow, automatic: Bool, commit: @escaping (NSTextView) -> Void) {
        rememberFocusedEditor(editor)
        let current = Request(editor: editor, window: window, now: environment.now(), automatic: automatic, commit: commit)
        request = current
        installObservers()
        transition(.activating)
        guard request === current else { return }
        if automatic, !automaticTargetReady(editor) { finish(.cancelled); return }
        if !environment.appIsActive() { environment.activate() }
        guard request === current else { return }
        advance()
        if request === current { scheduleCheck() }
    }

    func cancel(notify: Bool = true) {
        clearLifecycleIntent()
        if request != nil { finish(notify ? .cancelled : nil) }
        // A dismissed notice/new request starts a new state stream even if the
        // previous attempt ended in the same activation or failure state.
        if !notify { state = .idle }
    }

    /// Also called by deterministic diagnostics after delivering a simulated
    /// AppKit activation notification. It never issues a second activation.
    func activationStateDidChange() { advance() }

    func keyWindowDidChange(to window: NSWindow) {
        guard let current = request else { return }
        if current.window !== window { finish(.cancelled) }
        else { advance() }
    }

    /// Called only after an explicit local mouse-down, or a real return to this
    /// application. No global key interception, event replay or background focus.
    func editorClickIntent(view: NSView?, in window: NSWindow?) {
        cancelAutomaticWork()
        guard request == nil, let view, let window, view.window === window else { return }
        var candidate: NSView? = view
        while let value = candidate {
            if let editor = value as? NSTextView {
                guard editor.isEditable else { return }
                queueLifecycle(target: editor, window: window, explicitClick: true); return
            }
            if let control = value as? NSControl {
                guard control.isEnabled else { return }
                queueLifecycle(target: control, window: window, explicitClick: true); return
            }
            candidate = value.superview
        }
    }

    func applicationReturned() {
        guard environment.ownAppIsFrontmost() else { return }
        if request?.automatic == true { advance(); return }
        guard request == nil, lifecycleIntent == nil, let editor = resolveEditor(), let window = editor.window else { return }
        queueLifecycle(target: editor.isFieldEditor ? ((fieldEditorOwner(editor) as NSView?) ?? editor) : editor, window: window, explicitClick: false)
    }

    func applicationDeparted() { cancelAutomaticWork() }

    /// A later key in the old editor is a newer user action than a missed
    /// click. Observe only routing identity, never characters or modifiers.
    func keyboardInteraction(in window: NSWindow?) {
        guard let window, let responder = window.firstResponder else { return }
        priorResponderInteracted(responder, in: window)
    }

    private func priorEditorInteracted(_ editor: NSTextView) {
        guard let window = editor.window else { return }
        priorResponderInteracted(editor, in: window)
    }

    private func priorResponderInteracted(_ responder: NSResponder, in window: NSWindow) {
        guard let intent = lifecycleIntent, intent.explicitClick, !intent.requestedFocus,
              intent.priorResponder === responder, intent.window === window,
              window.firstResponder === responder,
              let target = intent.target else { return }
        // Shared field editors retain their pointer when the native click has
        // already moved ownership to the intended field. Preserve that repair.
        if target === responder { return }
        if let editor = responder as? NSTextView,
           (target as? NSControl).map({ fieldEditorOwner(editor) === $0 }) == true { return }
        cancelAutomaticWork()
    }

    private func clearLifecycleIntent() {
        lifecycleIntent = nil; lifecycleGeneration &+= 1
        lifecycleInputObservers.forEach { NotificationCenter.default.removeObserver($0) }
        lifecycleInputObservers.removeAll()
    }

    private func cancelAutomaticWork() {
        clearLifecycleIntent()
        if request?.automatic == true { finish(.cancelled) }
    }

    private func queueLifecycle(target: NSView, window: NSWindow, explicitClick: Bool) {
        clearLifecycleIntent()
        let intent = LifecycleIntent(target: target, window: window, started: environment.now(), explicitClick: explicitClick)
        lifecycleIntent = intent
        // Per-intent observers cover IME commits and programmatic edits that
        // arrive without a new key event. No text contents are retained/read.
        if explicitClick, let editor = intent.priorResponder as? NSTextView {
            for (name, object) in [(NSText.didChangeNotification, editor as AnyObject),
                                   (NSTextView.didChangeSelectionNotification, editor as AnyObject),
                                   (NSTextStorage.didProcessEditingNotification, editor.textStorage as AnyObject)] {
                lifecycleInputObservers.append(NotificationCenter.default.addObserver(forName: name, object: object, queue: .main) { [weak self, weak intent, weak editor] _ in
                    MainActor.assumeIsolated {
                        guard let self, let intent, self.lifecycleIntent === intent, let editor else { return }
                        self.priorEditorInteracted(editor)
                    }
                })
            }
        }
        scheduleLifecycle(after: 0.15)
    }

    private func scheduleLifecycle(after delay: TimeInterval) {
        let expected = lifecycleGeneration
        environment.schedule(delay) { [weak self] in
            guard let self, self.lifecycleGeneration == expected, self.lifecycleIntent != nil else { return }
            self.reconcileLifecycle()
        }
    }

    private func reconcileLifecycle() {
        guard let intent = lifecycleIntent else { return }
        guard request == nil, environment.ownAppIsFrontmost(),
              let target = intent.target, let window = intent.window,
              target.window === window, target.superview === intent.parent,
              !target.isHiddenOrHasHiddenAncestor, !target.visibleRect.isEmpty,
              window.isVisible, !window.isMiniaturized, window.attachedSheet == nil, environment.modalWindow() == nil,
              environment.keyWindow() == nil || environment.keyWindow() === window else { cancelAutomaticWork(); return }
        let expired = environment.now() - intent.started >= timeout
        guard let editor = window.firstResponder as? NSTextView, eligible(editor),
              target === editor || (target as? NSControl).map({ fieldEditorOwner(editor) === $0 }) == true else {
            establishClickedFocus(intent, target: target, window: window, expired: expired)
            return
        }
        // Native composition is never committed/discarded automatically.
        guard !editor.hasMarkedText(), !environment.editorIsProcessingInput(editor) else {
            if expired { cancelAutomaticWork() } else { scheduleLifecycle(after: 0.05) }
            return
        }
        if environment.appIsActive(), environment.keyWindow() === window, window.isKeyWindow, environment.contextIsCurrent(editor) {
            cancelAutomaticWork(); return
        }
        guard !expired else { cancelAutomaticWork(); return }
        clearLifecycleIntent()
        beginRecovery(editor: editor, window: window, automatic: true, commit: { _ in })
    }

    /// An activation click can fail to focus its native target. Unlike a return
    /// notification, an exact click permits ONE focus attempt, but only while
    /// the responder observed before dispatch is unchanged. Never search a pane
    /// for an editor or replace a newer user's focus choice.
    private func establishClickedFocus(_ intent: LifecycleIntent, target: NSView, window: NSWindow, expired: Bool) {
        guard intent.explicitClick, !intent.requestedFocus, !expired,
              intent.hadPriorResponder ? (intent.priorResponder != nil && window.firstResponder === intent.priorResponder) : window.firstResponder == nil else {
            cancelAutomaticWork(); return
        }
        if let prior = intent.priorResponder as? NSTextView {
            if prior.isFieldEditor, fieldEditorOwner(prior) !== intent.priorControl { cancelAutomaticWork(); return }
            if prior.hasMarkedText() || environment.editorIsProcessingInput(prior) { scheduleLifecycle(after: 0.05); return }
        }
        if let editor = target as? NSTextView {
            guard editor.isEditable else { cancelAutomaticWork(); return }
            if editor.hasMarkedText() || environment.editorIsProcessingInput(editor) { scheduleLifecycle(after: 0.05); return }
        } else if let field = target as? NSTextField {
            guard field.isEnabled, field.isEditable else { cancelAutomaticWork(); return }
        } else { cancelAutomaticWork(); return }
        if !environment.appIsActive() {
            if !intent.requestedActivation { intent.requestedActivation = true; environment.activate() }
            if lifecycleIntent === intent { scheduleLifecycle(after: 0.05) }
            return
        }
        if environment.keyWindow() !== window || !window.isKeyWindow {
            if !intent.requestedKey { intent.requestedKey = true; environment.makeKey(window) }
            if lifecycleIntent === intent { scheduleLifecycle(after: 0.05) }
            return
        }
        intent.requestedFocus = true
        // These checks immediately precede the native focus call; synchronous
        // activation callbacks may have retired or replaced the click intent.
        guard lifecycleIntent === intent, environment.ownAppIsFrontmost() else { return }
        let accepted = environment.makeFirstResponder(window, target)
        guard lifecycleIntent === intent else { return }
        guard accepted else { cancelAutomaticWork(); return }
        scheduleLifecycle(after: 0.15)
    }

    private func automaticTargetReady(_ editor: NSTextView) -> Bool {
        environment.ownAppIsFrontmost() && !editor.hasMarkedText() && !environment.editorIsProcessingInput(editor)
    }

    private func eligible(_ editor: NSTextView) -> Bool {
        guard let window = editor.window, window.isVisible, !window.isMiniaturized,
              window.attachedSheet == nil, environment.modalWindow() == nil,
              editor.isEditable, !editor.isHiddenOrHasHiddenAncestor, !editor.visibleRect.isEmpty,
              window.firstResponder === editor else { return false }
        if editor.isFieldEditor, fieldEditorOwner(editor) == nil { return false }
        return true
    }

    /// A shared field editor belongs to a control, not to the transient clip
    /// view AppKit installs while editing. Ending editing removes that view.
    /// Only controls that confirm this exact editor may participate in recovery.
    private func fieldEditorOwner(_ editor: NSTextView) -> NSControl? {
        guard editor.isFieldEditor, let control = editor.delegate as? NSControl,
              control.currentEditor() === editor, control.window === editor.window,
              control.isEnabled, !control.isHiddenOrHasHiddenAncestor, !control.visibleRect.isEmpty else { return nil }
        return control
    }

    private func ownsEditor(_ current: Request, editor: NSTextView, window: NSWindow) -> Bool {
        guard editor.window === window else { return false }
        if current.usesFieldEditor {
            guard let control = current.control, fieldEditorOwner(editor) === control,
                  control.superview === current.controlParent else { return false }
            return true
        }
        return !editor.isFieldEditor && editor.superview === current.parent
    }

    private func advance() {
        guard !advancing, let current = request else { return }
        advancing = true
        defer { advancing = false }
        guard let editor = current.editor, let window = current.window,
              ownsEditor(current, editor: editor, window: window), eligible(editor) else {
            finish(.cancelled); return
        }
        if let keyWindow = environment.keyWindow(), keyWindow !== window {
            finish(.cancelled); return
        }
        let expired = environment.now() - current.started >= timeout
        if current.automatic {
            guard environment.ownAppIsFrontmost() else { finish(.cancelled); return }
            guard automaticTargetReady(editor) else {
                if expired { finish(.cancelled) }
                return
            }
        }
        guard environment.appIsActive() else {
            if expired { finish(.failed(.activationUnavailable)) }
            return
        }
        guard environment.keyWindow() === window, window.isKeyWindow else {
            if expired { finish(.failed(.windowUnavailable)); return }
            if !current.requestedKey { current.requestedKey = true; environment.makeKey(window) }
            return
        }
        if !current.rebound {
            if current.automatic, environment.contextIsCurrent(editor) { finish(.reconnected); return }
            if !current.automatic { current.commit(editor) }
            guard request === current, readyToRebind(current, editor: editor, window: window) else {
                if request === current { finish(.cancelled) }; return
            }
            let selection = editor.selectedRanges
            transition(.verifying)
            guard request === current, readyToRebind(current, editor: editor, window: window) else {
                if request === current { finish(.cancelled) }; return
            }
            rebinding = true
            let released = environment.makeFirstResponder(window, nil)
            rebinding = false
            guard request === current else { return }
            guard released else { finish(.failed(.responderRejected)); return }
            guard readyToRebind(current, editor: editor, window: window, releasedResponder: true) else {
                finish(.cancelled); return
            }
            rebinding = true
            // A text field must resume editing through its owning control.
            // Reusing the detached shared NSTextView bypasses cell setup and
            // cannot restore the control's editing session.
            let accepted = environment.makeFirstResponder(window, current.usesFieldEditor ? current.control : editor)
            rebinding = false
            guard request === current else { return }
            guard accepted else { finish(.failed(.responderRejected)); return }
            let rebound = current.usesFieldEditor ? current.control?.currentEditor() as? NSTextView : editor
            guard let rebound, readyToRebind(current, editor: rebound, window: window) else { finish(.cancelled); return }
            current.editor = rebound
            // End-editing callbacks may update the control's value. Restore the
            // caret within the current native document without replacing it.
            let length = rebound.textStorage?.length ?? 0
            rebound.selectedRanges = selection.map { value in
                let range = value.rangeValue
                let location = min(range.location, length)
                return NSValue(range: NSRange(location: location, length: min(range.length, length - location)))
            }
            guard request === current else { return }
            rememberFocusedEditor(rebound)
            current.rebound = true
            // The input context can be installed after makeFirstResponder returns.
            return
        }
        if environment.contextIsCurrent(editor) { finish(.reconnected) }
        else if expired { finish(.failed(.inputContextUnavailable)) }
    }

    private func readyToRebind(_ current: Request, editor: NSTextView, window: NSWindow, releasedResponder: Bool = false) -> Bool {
        if current.automatic, !automaticTargetReady(editor) { return false }
        guard environment.appIsActive(), environment.keyWindow() === window, window.isKeyWindow,
              window.isVisible, !window.isMiniaturized, window.attachedSheet == nil, environment.modalWindow() == nil else { return false }
        if releasedResponder, current.usesFieldEditor {
            guard let control = current.control, control.window === window,
                  control.superview === current.controlParent, control.isEnabled,
                  !control.isHiddenOrHasHiddenAncestor, !control.visibleRect.isEmpty,
                  control.currentEditor() == nil else { return false }
            return window.firstResponder == nil || window.firstResponder === window
        }
        guard ownsEditor(current, editor: editor, window: window), editor.isEditable,
              !editor.isHiddenOrHasHiddenAncestor, !editor.visibleRect.isEmpty else { return false }
        return releasedResponder ? (window.firstResponder == nil || window.firstResponder === window) : window.firstResponder === editor
    }

    private func scheduleCheck() {
        guard request != nil else { return }
        let expected = generation
        environment.schedule(0.05) { [weak self] in
            guard let self, self.generation == expected, self.request != nil else { return }
            self.advance()
            self.scheduleCheck()
        }
    }

    /// Remember native editing ownership and observe key routing without replay.
    /// Text fields announce begin-editing only after their first text change;
    /// the outgoing key window also covers an empty field the user only clicked.
    /// These lifetime observers survive completion of an individual recovery.
    private func installTrackingObservers() {
        let center = NotificationCenter.default
        mouseMonitor = NSEvent.addLocalMonitorForEvents(matching: [.leftMouseDown, .rightMouseDown, .otherMouseDown, .keyDown]) { [weak self] event in
            guard let self else { return event }
            if event.type == .keyDown { self.keyboardInteraction(in: event.window); return event }
            if event.type == .leftMouseDown, let window = event.window, let content = window.contentView {
                let point = content.superview?.convert(event.locationInWindow, from: nil) ?? event.locationInWindow
                self.editorClickIntent(view: content.hitTest(point), in: window)
            } else { self.cancelAutomaticWork() }
            return event
        }
        for name in [NSApplication.didBecomeActiveNotification, NSWindow.didBecomeKeyNotification] {
            trackingObservers.append(center.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
                MainActor.assumeIsolated { self?.applicationReturned() }
            })
        }
        trackingObservers.append(center.addObserver(forName: NSApplication.didResignActiveNotification, object: NSApp, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.applicationDeparted() }
        })
        workspaceObservers.append(NSWorkspace.shared.notificationCenter.addObserver(forName: NSWorkspace.didActivateApplicationNotification, object: nil, queue: .main) { [weak self] note in
            MainActor.assumeIsolated {
                guard let self, let application = note.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication else { return }
                if application.processIdentifier == ProcessInfo.processInfo.processIdentifier { self.applicationReturned() }
                else { self.applicationDeparted() }
            }
        })
        trackingObservers.append(center.addObserver(forName: NSControl.textDidBeginEditingNotification, object: nil, queue: .main) { [weak self] note in
            MainActor.assumeIsolated {
                guard let self, let control = note.object as? NSControl,
                      let editor = control.currentEditor() as? NSTextView,
                      self.fieldEditorOwner(editor) === control, self.eligible(editor) else { return }
                self.rememberFocusedEditor(editor)
            }
        })
        trackingObservers.append(center.addObserver(forName: NSWindow.didResignKeyNotification, object: nil, queue: .main) { [weak self] note in
            MainActor.assumeIsolated {
                guard let self, let window = note.object as? NSWindow else { return }
                if self.lifecycleIntent?.window === window { self.cancelAutomaticWork() }
                guard
                      self.environment.keyWindow() == nil || self.environment.keyWindow() === window,
                      self.request == nil || self.request?.window === window,
                      let editor = window.firstResponder as? NSTextView, self.eligible(editor) else { return }
                self.rememberFocusedEditor(editor)
            }
        })
    }

    private func installObservers() {
        guard observesNotifications, let window = request?.window else { return }
        for (name, object) in [(NSApplication.didBecomeActiveNotification, NSApp as AnyObject),
                               (NSTextInputContext.keyboardSelectionDidChangeNotification, nil)] as [(Notification.Name, AnyObject?)] {
            observers.append(NotificationCenter.default.addObserver(forName: name, object: object, queue: .main) { [weak self] _ in
                MainActor.assumeIsolated { self?.advance() }
            })
        }
        observers.append(NotificationCenter.default.addObserver(forName: NSWindow.didBecomeKeyNotification, object: nil, queue: .main) { [weak self] note in
            MainActor.assumeIsolated {
                if let window = note.object as? NSWindow { self?.keyWindowDidChange(to: window) }
            }
        })
        for (name, object) in [(NSApplication.didResignActiveNotification, NSApp as AnyObject),
                               (NSWindow.didResignKeyNotification, window as AnyObject),
                               (NSWindow.willCloseNotification, window as AnyObject)] {
            observers.append(NotificationCenter.default.addObserver(forName: name, object: object, queue: .main) { [weak self] _ in
                MainActor.assumeIsolated { self?.cancel() }
            })
        }
    }

    private func finish(_ next: State?) {
        request = nil; generation &+= 1
        observers.forEach { NotificationCenter.default.removeObserver($0) }; observers.removeAll()
        if let next { transition(next) }
    }

    private func transition(_ next: State) {
        guard state != next else { return }
        state = next; onStateChange?(next)
    }
}
