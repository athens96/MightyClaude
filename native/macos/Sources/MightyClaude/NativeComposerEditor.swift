import AppKit
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
}

final class ComposerTextView: NSTextView {
    var onFocusChange: ((Bool) -> Void)?
    var onInputFinished: (() -> Void)?
    var onPasteAttachments: ((NSPasteboard) -> Void)?
    private var inputMutationDepth = 0
    var isUpdatingInput: Bool { inputMutationDepth > 0 }

    /// A button click need not resign the text view. Commit its current visible
    /// syllable before AppStore captures and clears the draft, without sending
    /// an Enter key to the input method or changing keyboard focus.
    func prepareForSubmission() {
        performInputTransaction {
            if hasMarkedText() {
                unmarkText()
                inputContext?.discardMarkedText()
            }
        }
    }

    // One key can commit the preceding syllable and begin the next marked
    // range through several sequential NSTextInputClient callbacks. Publishing
    // between those callbacks lets @Published/SwiftUI updates re-enter AppKit
    // before the input context has finished interpreting that key.
    override func keyDown(with event: NSEvent) {
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
        if accepted { onFocusChange?(false) }
        return accepted
    }

    override func setMarkedText(_ string: Any, selectedRange: NSRange, replacementRange: NSRange) {
        performInputTransaction { super.setMarkedText(string, selectedRange: selectedRange, replacementRange: replacementRange) }
    }

    override func insertText(_ string: Any, replacementRange: NSRange) {
        performInputTransaction { super.insertText(string, replacementRange: replacementRange) }
    }

    override func unmarkText() {
        performInputTransaction { super.unmarkText() }
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
