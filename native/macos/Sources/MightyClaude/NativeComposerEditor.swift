import AppKit
import MightyCore
import SwiftUI
import UniformTypeIdentifiers

/// AppKit owns the editable document and input method. SwiftUI receives a
/// coalesced draft snapshot after native input finishes, never from inside an
/// input-method callback. Only explicit model changes replace native storage.
struct NativeComposerEditor: NSViewRepresentable {
    @Binding var text: String
    var monospaced = false
    var accessibilityLabel = "메시지"
    var accessibilityIdentifier = ""
    var onFocusChange: (Bool) -> Void = { _ in }
    var onPasteAttachments: ((NSPasteboard) -> Void)?
    var inputController: ComposerInputController?
    var canSubmit: () -> Bool = { false }
    var onSubmit: (Bool) -> Void = { _ in }
    var onNavigationKey: ((ComposerNavigationKey) -> Bool)?

    func makeCoordinator() -> Coordinator { Coordinator(text: $text) }

    func makeNSView(context: Context) -> NSScrollView {
        let editor = ComposerTextView(frame: NSRect(x: 0, y: 0, width: 500, height: 22))
        editor.isEditable = true; editor.isSelectable = true; editor.isRichText = false
        editor.importsGraphics = false; editor.allowsUndo = true; editor.drawsBackground = false
        editor.isVerticallyResizable = true; editor.isHorizontallyResizable = false
        editor.minSize = .zero
        editor.maxSize = NSSize(width: CGFloat.greatestFiniteMagnitude, height: CGFloat.greatestFiniteMagnitude)
        editor.autoresizingMask = [.width]
        editor.textContainer?.widthTracksTextView = true
        editor.textContainerInset = NSSize(width: 0, height: 1)
        editor.font = monospaced ? .monospacedSystemFont(ofSize: 13, weight: .regular) : .systemFont(ofSize: 13)
        editor.textColor = .labelColor; editor.insertionPointColor = .labelColor
        editor.isAutomaticQuoteSubstitutionEnabled = false
        editor.isAutomaticDashSubstitutionEnabled = false
        editor.string = text
        context.coordinator.attach(editor)
        inputController?.editor = editor
        context.coordinator.inputController = inputController
        let scroll = NSScrollView(frame: editor.frame)
        scroll.drawsBackground = false; scroll.borderType = .noBorder
        scroll.hasVerticalScroller = true; scroll.hasHorizontalScroller = false
        scroll.autohidesScrollers = true; scroll.scrollerStyle = .overlay
        scroll.documentView = editor
        return scroll
    }

    func updateNSView(_ scroll: NSScrollView, context: Context) {
        let coordinator = context.coordinator
        coordinator.text = $text
        guard let editor = coordinator.editor else { return }
        editor.onFocusChange = onFocusChange
        editor.onPasteAttachments = onPasteAttachments
        editor.canSubmit = canSubmit
        editor.onSubmit = onSubmit
        editor.onNavigationKey = onNavigationKey
        inputController?.editor = editor
        coordinator.inputController = inputController
        editor.setAccessibilityLabel(accessibilityLabel)
        editor.setAccessibilityIdentifier(accessibilityIdentifier)
        coordinator.receiveModelText(text)
    }

    static func dismantleNSView(_ scroll: NSScrollView, coordinator: Coordinator) {
        coordinator.detach()
    }

    @MainActor
    final class Coordinator: NSObject, NSTextViewDelegate {
        var text: Binding<String>
        weak var editor: ComposerTextView?
        weak var inputController: ComposerInputController?
        private var observedModelText: String
        private var deferredModelText: String?
        private var applyingModel = false
        private var publicationScheduled = false
        private var publicationGeneration: UInt64 = 0
        private let enqueue: (@escaping () -> Void) -> Void

        init(text: Binding<String>, enqueue: @escaping (@escaping () -> Void) -> Void = { action in DispatchQueue.main.async { action() } }) {
            self.text = text; observedModelText = text.wrappedValue; self.enqueue = enqueue
        }

        func attach(_ editor: ComposerTextView) {
            self.editor = editor
            editor.delegate = self
            editor.onInputFinished = { [weak self] in self?.publishNativeText() }
            editor.onFlush = { [weak self] in self?.flushNativeText() }
            editor.onReplaceDraft = { [weak self] value in self?.replaceDraft(value) }
        }

        func detach() {
            // A pane may disappear before the queued publication runs.
            if let editor { InputSessionRecoveryCoordinator.shared.retireEditor(editor) }
            flushNativeText()
            cancelPublication()
            if inputController?.editor === editor { inputController?.editor = nil }
            editor?.delegate = nil
            editor?.onFocusChange = nil
            editor?.onInputFinished = nil
            editor?.onFlush = nil
            editor?.onReplaceDraft = nil
            editor?.onPasteAttachments = nil
            editor?.onSubmit = nil
            editor?.canSubmit = { false }
            editor?.onNavigationKey = nil
            editor = nil
        }

        func receiveModelText(_ value: String) {
            guard value == text.wrappedValue, value != observedModelText else { return }
            observedModelText = value
            guard let editor else { return }
            if editor.hasMarkedText() || editor.isUpdatingInput {
                deferredModelText = value
                return
            }
            deferredModelText = nil
            cancelPublication()
            applyModelText(value)
        }

        func textDidChange(_ notification: Notification) { publishNativeText() }

        func textView(_ textView: NSTextView, doCommandBy commandSelector: Selector) -> Bool {
            guard let editor, textView === editor else { return false }
            return editor.handleNativeCommand(commandSelector)
        }

        /// Notifications can arrive before AppKit installs its marked range or
        /// between several replacements in one event. A main-queue handoff is
        /// the boundary; no @Published write occurs in those callbacks.
        func publishNativeText() {
            guard !applyingModel, editor != nil, !publicationScheduled else { return }
            publicationScheduled = true
            let generation = publicationGeneration
            enqueue { [weak self] in
                guard let self, self.publicationGeneration == generation else { return }
                self.publicationScheduled = false
                guard self.editor?.isUpdatingInput == false else { self.publishNativeText(); return }
                self.flushNativeText()
            }
        }

        /// Explicit actions flush before reading the draft, including a Return
        /// arriving before SwiftUI has rendered the preceding character.
        func flushNativeText() {
            guard !applyingModel, let editor else { return }
            cancelPublication()
            // The store can change before SwiftUI calls updateNSView. Observe
            // that write here so an older queued native snapshot cannot erase it.
            if text.wrappedValue != observedModelText {
                observedModelText = text.wrappedValue
                deferredModelText = observedModelText
            }
            if let pending = deferredModelText {
                guard !editor.hasMarkedText(), !editor.isUpdatingInput else { return }
                deferredModelText = nil
                applyModelText(pending)
                return
            }
            let value = editor.string
            observedModelText = value
            if text.wrappedValue != value { text.wrappedValue = value }
        }

        func replaceDraft(_ value: String) {
            // A new explicit command supersedes an older deferred model write.
            // Clear it before unmarkText can notify the delegate.
            deferredModelText = nil
            cancelPublication()
            observedModelText = text.wrappedValue
            applyingModel = true
            editor?.replaceNativeDraft(value)
            applyingModel = false
            flushNativeText()
        }

        private func cancelPublication() {
            publicationGeneration &+= 1
            publicationScheduled = false
        }

        private func applyModelText(_ value: String) {
            guard let editor, editor.string != value else { return }
            applyingModel = true
            defer { applyingModel = false }
            let selection = editor.selectedRange()
            editor.string = value
            editor.undoManager?.removeAllActions()
            let location = min(selection.location, (value as NSString).length)
            editor.setSelectedRange(NSRange(location: location, length: 0))
            editor.didChangeText()
        }
    }
}

@MainActor
final class ComposerInputController {
    weak var editor: ComposerTextView? {
        didSet {
            guard oldValue !== editor else { return }
            for observer in Array(editorObservers.values) { observer(editor) }
        }
    }
    private var editorObservers: [UUID: (ComposerTextView?) -> Void] = [:]
    func prepareForSubmission() { editor?.prepareForSubmission() }
    func replaceDraft(_ text: String) { editor?.replaceDraft(text) }
    func observeEditor(_ observer: @escaping (ComposerTextView?) -> Void) -> UUID {
        let token = UUID(); editorObservers[token] = observer; observer(editor); return token
    }
    func removeEditorObserver(_ token: UUID) { editorObservers.removeValue(forKey: token) }
}

/// A normal NSTextView with attachment support and commands delivered by its
/// delegate after the native input context interprets the event. It never
/// composes Hangul itself or changes activation/focus while processing input.
final class ComposerTextView: NSTextView, InputSessionRecoveryInputTransaction {
    var onFocusChange: ((Bool) -> Void)?
    var onInputFinished: (() -> Void)?
    var onFlush: (() -> Void)?
    var onReplaceDraft: ((String) -> Void)?
    var onPasteAttachments: ((NSPasteboard) -> Void)?
    var canSubmit: () -> Bool = { false }
    var onSubmit: ((Bool) -> Void)?
    var onNavigationKey: ((ComposerNavigationKey) -> Bool)?
    private var inputMutationDepth = 0
    private var keyEvent: NSEvent?
    private var keyBeganWithMarkedText = false
    private var keyReceivedNativeText = false
    private var keyConsumedApplicationCommand = false
    var isUpdatingInput: Bool { inputMutationDepth > 0 }

    func prepareForSubmission() {
        performInputTransaction {
            if hasMarkedText() { unmarkText(); inputContext?.discardMarkedText() }
        }
        onFlush?()
    }

    func replaceDraft(_ text: String) {
        if let onReplaceDraft { onReplaceDraft(text) }
        else { replaceNativeDraft(text); onFlush?() }
    }

    fileprivate func replaceNativeDraft(_ text: String) {
        performInputTransaction {
            if hasMarkedText() { unmarkText(); inputContext?.discardMarkedText() }
            string = text
            undoManager?.removeAllActions()
            setSelectedRange(NSRange(location: (text as NSString).length, length: 0))
            didChangeText()
        }
    }

    override func keyDown(with event: NSEvent) {
        performNativeKeyEvent(event) { super.keyDown(with: event) }
    }

    // NSTextView otherwise uses the first click in an inactive window only
    // for activation. Let this explicit editor click reach native mouseDown
    // so AppKit focuses the clicked composer and positions its caret at once.
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool {
        isEditable && !isHiddenOrHasHiddenAncestor
    }

    /// ⌘Return is a composer shortcut, but still goes through NSTextView's
    /// native key interpretation before the delegate may choose to send it.
    override func performKeyEquivalent(with event: NSEvent) -> Bool {
        let modifiers = event.modifierFlags.intersection([.command, .control, .option, .shift])
        if event.type == .keyDown, modifiers == .command, [36, 76].contains(event.keyCode),
           let window, event.window === window, window.firstResponder === self {
            keyDown(with: event)
            return true
        }
        return super.performKeyEquivalent(with: event)
    }

    /// Also used by deterministic diagnostics to deliver the commands an input
    /// context produced, without driving the user's keyboard or focus.
    func performNativeKeyEvent(_ event: NSEvent, interpret: () -> Void) {
        let previousEvent = keyEvent, previousMarked = keyBeganWithMarkedText
        let previousNativeText = keyReceivedNativeText, previousCommand = keyConsumedApplicationCommand
        keyEvent = event; keyBeganWithMarkedText = hasMarkedText()
        keyReceivedNativeText = false; keyConsumedApplicationCommand = false
        InputMethodMonitor.shared.keyBegan(event, in: self)
        defer {
            InputMethodMonitor.shared.keyEnded()
            keyEvent = previousEvent; keyBeganWithMarkedText = previousMarked
            keyReceivedNativeText = previousNativeText; keyConsumedApplicationCommand = previousCommand
        }
        performInputTransaction(interpret)
    }

    func handleNativeCommand(_ selector: Selector) -> Bool {
        let name = NSStringFromSelector(selector)
        let knownCommands = ["insertNewline:", "insertNewlineIgnoringFieldEditor:", "insertParagraphSeparator:", "insertLineBreak:", "noop:", "moveUp:", "moveDown:", "insertTab:", "cancelOperation:"]
        let commandLabel = knownCommands.contains(name) ? name : "other"
        let marked = hasMarkedText()
        let currentEvent = NSApp.currentEvent
        var decision = "native.no_bound_key"
        defer {
            // Only routing metadata: never text, selections, key characters,
            // event descriptions, or a retained last-key approximation.
            InputMethodMonitor.shared.record("command selector=\(commandLabel) decision=\(decision) boundKey=\(keyEvent != nil) currentKey=\(currentEvent?.type == .keyDown) currentWindowMatches=\(currentEvent?.window === window) marked=\(marked) focused=\(window?.firstResponder === self)")
        }
        guard let event = keyEvent else { return false }
        decision = "native.responder_mismatch"
        guard let window, event.window === window,
              window.firstResponder === self, isEditable, !isHiddenOrHasHiddenAncestor else { return false }
        decision = "native.marked_text"
        guard !marked else { return false }
        let modifiers = event.modifierFlags.intersection([.command, .control, .option, .shift])
        // AppKit can emit noop: for ⌘Return. Treat that
        // interpreted command as this shortcut only for the exact original
        // event. If an IME consumed the key without issuing a command, this
        // delegate is never called and no submit is manufactured.
        let isCommandReturn = modifiers == .command && [36, 76].contains(event.keyCode)
            && !keyReceivedNativeText && !keyBeganWithMarkedText
        let isReturn = ["insertNewline:", "insertNewlineIgnoringFieldEditor:", "insertParagraphSeparator:", "insertLineBreak:"].contains(name)
            || (name == "noop:" && isCommandReturn)
        // An input method can commit its final syllable and then explicitly
        // deliver Return in the same event. That command sends immediately;
        // requiring a second key loses the native meaning of the first Return.
        // Commit-only events and candidate-navigation commands stay with AppKit.
        decision = "native.candidate_command"
        guard !keyBeganWithMarkedText || isReturn else { return false }
        let navigation: ComposerNavigationKey? = switch name {
        case "moveUp:": .up
        case "moveDown:": .down
        case "insertTab:": .select
        case "cancelOperation:": .dismiss
        default: isReturn ? .select : nil
        }
        if keyConsumedApplicationCommand, navigation != nil || isReturn { decision = "consume.duplicate"; return true }
        // Native interpretation is complete for this command, so consumers must
        // see the actual document, even before its queued draft update runs.
        if modifiers.isEmpty, let navigation {
            onFlush?()
            if onNavigationKey?(navigation) == true { keyConsumedApplicationCommand = true; decision = "consume.navigation"; return true }
        }
        decision = "native.command_or_modifiers"
        guard isReturn, modifiers.isEmpty || modifiers == .command else { return false }
        keyConsumedApplicationCommand = true
        onFlush?()
        decision = event.isARepeat ? "consume.repeat" : "consume.ineligible"
        if !event.isARepeat, canSubmit() { decision = "consume.submit"; onSubmit?(modifiers == .command) }
        return true
    }

    func performInputTransaction(_ body: () -> Void) {
        inputMutationDepth += 1
        defer {
            inputMutationDepth -= 1
            if inputMutationDepth == 0 { onInputFinished?() }
        }
        body()
    }

    override func becomeFirstResponder() -> Bool {
        let accepted = super.becomeFirstResponder()
        if accepted { publishFocus() }
        return accepted
    }
    override func resignFirstResponder() -> Bool {
        let accepted = super.resignFirstResponder()
        if accepted {
            InputSessionRecoveryCoordinator.shared.forgetEditor(self)
            publishFocus()
        }
        return accepted
    }
    private func publishFocus() {
        DispatchQueue.main.async { [weak self] in
            guard let self else { return }
            let focused = self.window?.firstResponder === self
            if focused { InputSessionRecoveryCoordinator.shared.rememberFocusedEditor(self) }
            self.onFocusChange?(focused)
        }
    }

    // Passive observations preserve the original AppKit callback and ranges.
    override func setMarkedText(_ string: Any, selectedRange: NSRange, replacementRange: NSRange) {
        if keyEvent != nil { keyReceivedNativeText = true }
        InputMethodMonitor.shared.noteMarkedText(string, selectedRange: selectedRange, in: self)
        super.setMarkedText(string, selectedRange: selectedRange, replacementRange: replacementRange)
        onInputFinished?()
    }
    override func insertText(_ string: Any, replacementRange: NSRange) {
        if keyEvent != nil { keyReceivedNativeText = true }
        super.insertText(string, replacementRange: replacementRange)
        InputMethodMonitor.shared.noteInsert(string, replacementRange: replacementRange, source: InputMethodMonitor.currentInputSourceID(), in: self)
    }
    override func unmarkText() {
        InputMethodMonitor.shared.noteUnmark(in: self)
        super.unmarkText()
        onInputFinished?()
    }
    override func firstRect(forCharacterRange range: NSRange, actualRange: NSRangePointer?) -> NSRect {
        let rect = super.firstRect(forCharacterRange: range, actualRange: actualRange)
        InputMethodMonitor.shared.noteFirstRect(rect, range: range)
        return rect
    }

    override var readablePasteboardTypes: [NSPasteboard.PasteboardType] { [.fileURL, .png, .tiff] + super.readablePasteboardTypes }
    override func paste(_ sender: Any?) {
        let board = NSPasteboard.general
        if let onPasteAttachments, board.types?.contains(where: Self.isAttachmentType) == true { onPasteAttachments(board) }
        else { super.paste(sender) }
    }
    override func readSelection(from pboard: NSPasteboard, type: NSPasteboard.PasteboardType) -> Bool {
        if Self.isAttachmentType(type), let onPasteAttachments { onPasteAttachments(pboard); return true }
        return super.readSelection(from: pboard, type: type)
    }
    private static func isAttachmentType(_ type: NSPasteboard.PasteboardType) -> Bool { type == .fileURL || UTType(type.rawValue)?.conforms(to: .image) == true }
}
