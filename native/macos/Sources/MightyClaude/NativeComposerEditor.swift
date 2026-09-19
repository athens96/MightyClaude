import AppKit
import MightyCore
import SwiftUI
import UniformTypeIdentifiers

/// Owns a stable native editor. Unrelated SwiftUI updates must not replace the
/// input method's marked text with a stale value of the draft.
struct NativeComposerEditor: NSViewRepresentable {
    @Binding var text: String
    var monospaced = false
    var accessibilityLabel = "메시지"
    var accessibilityIdentifier = ""
    var onFocusChange: (Bool) -> Void = { _ in }
    var onPasteAttachments: ((NSPasteboard) -> Void)?
    var inputController: ComposerInputController?

    func makeCoordinator() -> Coordinator { Coordinator(text: $text) }

    func makeNSView(context: Context) -> NSScrollView {
        let storage = NSTextStorage()
        let layout = NSLayoutManager()
        let container = NSTextContainer(size: NSSize(width: 500, height: CGFloat.greatestFiniteMagnitude))
        container.widthTracksTextView = true
        storage.addLayoutManager(layout); layout.addTextContainer(container)
        let editor = ComposerTextView(frame: NSRect(x: 0, y: 0, width: 500, height: 22), textContainer: container)
        editor.isEditable = true; editor.isSelectable = true; editor.isRichText = false
        editor.importsGraphics = false; editor.allowsUndo = true; editor.drawsBackground = false
        editor.isVerticallyResizable = true; editor.isHorizontallyResizable = false
        editor.minSize = .zero
        editor.maxSize = NSSize(width: CGFloat.greatestFiniteMagnitude, height: CGFloat.greatestFiniteMagnitude)
        editor.autoresizingMask = [.width]
        editor.textContainerInset = NSSize(width: 0, height: 1)
        editor.font = monospaced ? .monospacedSystemFont(ofSize: 13, weight: .regular) : .systemFont(ofSize: 13)
        editor.textColor = .labelColor; editor.insertionPointColor = .labelColor
        editor.isAutomaticQuoteSubstitutionEnabled = false
        editor.isAutomaticDashSubstitutionEnabled = false
        editor.string = text
        editor.delegate = context.coordinator
        editor.onInputFinished = { [weak coordinator = context.coordinator] in coordinator?.publishNativeText() }
        context.coordinator.editor = editor
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
        inputController?.editor = editor
        editor.setAccessibilityLabel(accessibilityLabel)
        editor.setAccessibilityIdentifier(accessibilityIdentifier)
        coordinator.receiveModelText(text)
    }

    static func dismantleNSView(_ scroll: NSScrollView, coordinator: Coordinator) {
        coordinator.editor?.delegate = nil
        coordinator.editor?.onFocusChange = nil
        coordinator.editor?.onInputFinished = nil
        coordinator.editor?.onPasteAttachments = nil
        coordinator.editor = nil
    }

    final class Coordinator: NSObject, NSTextViewDelegate {
        var text: Binding<String>
        weak var editor: ComposerTextView?
        private var observedModelText: String
        private var deferredModelText: String?
        private var applyingModel = false

        init(text: Binding<String>) { self.text = text; observedModelText = text.wrappedValue }

        func receiveModelText(_ value: String) {
            // Our own edit echoed through the binding or a store refresh is not
            // an instruction to replace storage or clear its marked range.
            guard value != observedModelText else { return }
            observedModelText = value
            guard let editor else { return }
            if editor.hasMarkedText() || editor.isUpdatingInput { deferredModelText = value; return }
            deferredModelText = nil
            applyModelText(value)
        }

        func textDidChange(_ notification: Notification) { publishNativeText() }

        func publishNativeText() {
            guard !applyingModel, let editor, !editor.isUpdatingInput else { return }
            if let pending = deferredModelText {
                guard !editor.hasMarkedText() else { return }
                deferredModelText = nil
                applyModelText(pending)
                return
            }
            // Include visible preedit text so the send button becomes available
            // for the first Korean syllable and never omits the final one. The
            // observed value is set first so its SwiftUI echo leaves IME alone.
            let value = editor.string
            observedModelText = value
            if text.wrappedValue != value { text.wrappedValue = value }
        }

        private func applyModelText(_ value: String) {
            guard let editor, editor.string != value else { return }
            editor.resetHangulFallback()
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

final class ComposerInputController {
    weak var editor: ComposerTextView?
    func prepareForSubmission() { editor?.prepareForSubmission() }
    /// Replaces the whole draft (used by slash completion) and parks the caret at the end.
    func replaceDraft(_ text: String) { editor?.replaceDraft(text) }
}

final class ComposerTextView: NSTextView {
    var onFocusChange: ((Bool) -> Void)?
    var onInputFinished: (() -> Void)?
    var onPasteAttachments: ((NSPasteboard) -> Void)?
    private var inputMutationDepth = 0
    // Composes Hangul in the app when the input method stops composing. It
    // stays silent while the input method replaces syllables itself.
    private var fallback = HangulFallback()
    private var isApplyingOwnEdit = false
    private var compositionBreakObservers: [NSObjectProtocol] = []
    var isUpdatingInput: Bool { inputMutationDepth > 0 }

    /// Drops the syllable being composed in the app: the text or the caret is
    /// about to change for a reason the fallback cannot follow.
    func resetHangulFallback() { fallback.reset() }

    /// A button click need not resign the text view. Commit its current visible
    /// syllable before AppStore captures and clears the draft, without sending
    /// an Enter key to the input method or changing keyboard focus.
    func prepareForSubmission() {
        fallback.reset()
        performInputTransaction {
            if hasMarkedText() {
                unmarkText()
                inputContext?.discardMarkedText()
            }
        }
    }

    func replaceDraft(_ text: String) {
        fallback.reset()
        performInputTransaction {
            if hasMarkedText() { unmarkText(); inputContext?.discardMarkedText() }
            string = text
            undoManager?.removeAllActions()
            setSelectedRange(NSRange(location: (text as NSString).length, length: 0))
            didChangeText()
        }
        onInputFinished?()
    }

    // One key can commit the preceding syllable and begin the next marked
    // range through several sequential NSTextInputClient callbacks. Publishing
    // between those callbacks lets @Published/SwiftUI updates re-enter AppKit
    // before the input context has finished interpreting that key.
    override func keyDown(with event: NSEvent) {
        InputMethodMonitor.shared.keyBegan(event)
        defer { InputMethodMonitor.shared.keyEnded() }
        performInputTransaction { super.keyDown(with: event) }
    }

    func performInputTransaction(_ body: () -> Void) {
        inputMutationDepth += 1
        defer { finishInputMutation() }
        body()
    }

    override func becomeFirstResponder() -> Bool {
        let accepted = super.becomeFirstResponder()
        if accepted { onFocusChange?(true) }
        return accepted
    }

    override func resignFirstResponder() -> Bool {
        let accepted = super.resignFirstResponder()
        if accepted { fallback.reset(); onFocusChange?(false) }
        return accepted
    }

    override func mouseDown(with event: NSEvent) {
        fallback.reset()
        super.mouseDown(with: event)
    }

    // Every caret move funnels through here. One we did not make (click, arrow
    // keys, a programmatic selection) parts the composing syllable from the
    // caret, so the app must forget it; our own edits move the caret too.
    override func setSelectedRanges(_ ranges: [NSValue], affinity: NSSelectionAffinity, stillSelecting: Bool) {
        if !isApplyingOwnEdit { fallback.reset() }
        super.setSelectedRanges(ranges, affinity: affinity, stillSelecting: stillSelecting)
    }

    // `insertText:` never arrives here and `deleteBackward:` is the one command
    // the fallback handles itself; every other command rewrites or moves text
    // the composing syllable can no longer be attached to.
    override func doCommand(by selector: Selector) {
        if selector != #selector(NSStandardKeyBindingResponding.deleteBackward(_:)) { fallback.reset() }
        super.doCommand(by: selector)
    }

    override func viewWillMove(toWindow newWindow: NSWindow?) {
        super.viewWillMove(toWindow: newWindow)
        observeCompositionBreaks(in: newWindow)
    }

    deinit { removeCompositionBreakObservers() }

    /// The input method can end its composition without the editor hearing of
    /// it: the app deactivates, the window stops being key, the input source
    /// changes. Whatever is still held here would be glued to the next jamo.
    private func observeCompositionBreaks(in window: NSWindow?) {
        removeCompositionBreakObservers()
        let center = NotificationCenter.default
        let ended: (Notification) -> Void = { [weak self] _ in self?.fallback.reset() }
        compositionBreakObservers = [
            center.addObserver(forName: NSApplication.didResignActiveNotification, object: nil, queue: .main, using: ended),
            center.addObserver(forName: NSTextInputContext.keyboardSelectionDidChangeNotification, object: nil, queue: .main, using: ended),
        ]
        if let window {
            compositionBreakObservers.append(center.addObserver(forName: NSWindow.didResignKeyNotification, object: window, queue: .main, using: ended))
        }
    }

    /// Tokens, not `removeObserver(self, name:)`: AppKit keeps its own
    /// registrations on the text view and those must survive.
    private func removeCompositionBreakObservers() {
        compositionBreakObservers.forEach(NotificationCenter.default.removeObserver)
        compositionBreakObservers = []
    }

    /// Both recorded failures had the app inactive and the text input system
    /// pointed at another context at the failing key. Composing here without
    /// one of those signals would fight an input method that is merely quiet.
    private var sessionSuspect: Bool { !NSApp.isActive || NSTextInputContext.current !== inputContext }

    override func setMarkedText(_ string: Any, selectedRange: NSRange, replacementRange: NSRange) {
        InputMethodMonitor.shared.noteMarkedText(string, selectedRange: selectedRange, in: self)
        performInputTransaction { super.setMarkedText(string, selectedRange: selectedRange, replacementRange: replacementRange) }
    }

    // Apple's 2-set Korean input composes by replacing the syllable it already
    // inserted; a dead input session sends the bare jamo instead. The fallback
    // rebuilds the syllable from those, and returns `passThrough` for
    // everything a live input method or another language sends.
    override func insertText(_ string: Any, replacementRange: NSRange) {
        let source = InputMethodMonitor.currentInputSourceID()
        let suspect = sessionSuspect
        let replacement = fallbackReplacement(for: string, replacementRange: replacementRange, source: source, suspect: suspect)
        applyingOwnEdit {
            if let replacement { super.insertText(replacement.edit.insert, replacementRange: replacement.range) }
            else { super.insertText(string, replacementRange: replacementRange) }
        }
        if InputMethodMonitor.shared.noteInsert(string, replacementRange: replacementRange, source: source, in: self) {
            // Uncombined jamo twice over: the session is dead in a way app state
            // does not show, so compose here from now on without waiting for it.
            fallback.confirmBroken()
        }
        if let replacement { InputMethodMonitor.shared.noteFallback(replacement.edit, engaged: fallback.hasEngaged, suspect: suspect, in: self) }
    }

    /// Backspace during in-app composition takes the syllable apart jamo by
    /// jamo (값 → 갑 → 가 → ㄱ); otherwise the editor deletes as usual.
    override func deleteBackward(_ sender: Any?) {
        let before = fallback
        guard selectedRange().length == 0, !hasMarkedText(), let edit = fallback.backspace(),
              let range = rangeBeforeCaret(edit.deleteBackward), documentHolds(edit.previous, at: range) else {
            fallback = before
            fallback.reset()
            super.deleteBackward(sender)
            return
        }
        applyingOwnEdit { super.insertText(edit.insert, replacementRange: range) }
        InputMethodMonitor.shared.noteFallback(edit, engaged: fallback.hasEngaged, suspect: sessionSuspect, in: self)
    }

    /// The edit to apply instead of the incoming call, with the range in front
    /// of the caret it replaces. A selection to overwrite, or a range whose text
    /// is not what the composer put there, ends the composition and lets the
    /// call through — the fallback is restored first, so nothing counts as
    /// engaged when nothing was applied.
    private func fallbackReplacement(for string: Any, replacementRange: NSRange, source: String?, suspect: Bool) -> (edit: HangulComposer.Edit, range: NSRange)? {
        guard selectedRange().length == 0 else { fallback.reset(); return nil }
        let text = (string as? String) ?? (string as? NSAttributedString)?.string ?? ""
        let before = fallback
        let decision = fallback.insert(text, hasReplacementRange: replacementRange.location != NSNotFound,
                                       hasMarkedText: hasMarkedText(),
                                       koreanSource: InputMethodSymptom.isTwoSetKoreanInputSource(source),
                                       sessionSuspect: suspect)
        guard case .apply(let edit) = decision else { return nil }
        guard let range = rangeBeforeCaret(edit.deleteBackward), documentHolds(edit.previous, at: range) else {
            fallback = before
            fallback.reset()
            return nil
        }
        return (edit, range)
    }

    private func rangeBeforeCaret(_ length: Int) -> NSRange? {
        let caret = selectedRange().location
        guard length >= 0, caret >= length, caret <= (string as NSString).length else { return nil }
        return NSRange(location: caret - length, length: length)
    }

    /// The units about to be replaced are the ones the composer put there. A
    /// mismatch means an edit the fallback does not follow (a word delete, a
    /// service, a drag) moved the text, so it must not delete anything.
    private func documentHolds(_ previous: String, at range: NSRange) -> Bool {
        (string as NSString).substring(with: range) == previous
    }

    /// One input transaction whose caret move is ours, so it does not read as a
    /// reason to stop composing.
    private func applyingOwnEdit(_ body: () -> Void) {
        isApplyingOwnEdit = true
        defer { isApplyingOwnEdit = false }
        performInputTransaction(body)
    }

    override func unmarkText() {
        InputMethodMonitor.shared.noteUnmark(in: self)
        performInputTransaction { super.unmarkText() }
    }

    override func firstRect(forCharacterRange range: NSRange, actualRange: NSRangePointer?) -> NSRect {
        let rect = super.firstRect(forCharacterRange: range, actualRange: actualRange)
        InputMethodMonitor.shared.noteFirstRect(rect, range: range)
        return rect
    }

    // AppKit can notify its delegate before a marked range is installed or
    // between commits within one key event. Publish after the outermost input
    // transaction completes; never replace storage mid-interpretation.
    private func finishInputMutation() {
        inputMutationDepth -= 1
        if inputMutationDepth == 0 { onInputFinished?() }
    }

    override var readablePasteboardTypes: [NSPasteboard.PasteboardType] {
        [.fileURL, .png, .tiff] + super.readablePasteboardTypes
    }

    override func paste(_ sender: Any?) {
        fallback.reset()
        let board = NSPasteboard.general
        if let onPasteAttachments, board.types?.contains(where: Self.isAttachmentType) == true { onPasteAttachments(board) }
        else { super.paste(sender) }
    }

    override func readSelection(from pboard: NSPasteboard, type: NSPasteboard.PasteboardType) -> Bool {
        if Self.isAttachmentType(type), let onPasteAttachments { onPasteAttachments(pboard); return true }
        return super.readSelection(from: pboard, type: type)
    }

    private static func isAttachmentType(_ type: NSPasteboard.PasteboardType) -> Bool {
        type == .fileURL || UTType(type.rawValue)?.conforms(to: .image) == true
    }
}
