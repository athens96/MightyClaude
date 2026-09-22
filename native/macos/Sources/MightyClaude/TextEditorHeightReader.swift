import AppKit
import SwiftUI

/// Presentation only. The controller supplies the exact native editor; this
/// view never searches SwiftUI geometry or intercepts keyboard events.
struct TextEditorHeightReader: NSViewRepresentable {
    let inputController: ComposerInputController
    @Binding var height: CGFloat
    var placeholder = ""

    func makeNSView(context: Context) -> HeightProbe { HeightProbe() }

    func updateNSView(_ view: HeightProbe, context: Context) {
        view.connect(to: inputController)
        view.placeholder = placeholder
        view.onHeightChange = { measured in
            if abs(height - measured) > 0.5 { height = measured }
        }
        view.scheduleMeasurement()
    }

    static func dismantleNSView(_ view: HeightProbe, coordinator: ()) { view.tearDown() }

    final class HeightProbe: NSView {
        var onHeightChange: ((CGFloat) -> Void)?
        var placeholder = "" { didSet { synchronizePlaceholder() } }
        private(set) weak var editor: NSTextView?
        private(set) var placeholderLabel: ComposerPlaceholderLabel?
        private var textObservers: [NSObjectProtocol] = []
        private weak var inputController: ComposerInputController?
        private var editorObserver: UUID?
        private var scheduled = false

        override func hitTest(_ point: NSPoint) -> NSView? { nil }
        override func viewDidMoveToWindow() {
            super.viewDidMoveToWindow()
            if window == nil { detachEditor() }
            else if let editor = inputController?.editor { attachEditor(editor) }
            scheduleMeasurement()
        }
        override func viewDidMoveToSuperview() { super.viewDidMoveToSuperview(); scheduleMeasurement() }
        override func setFrameSize(_ newSize: NSSize) { super.setFrameSize(newSize); scheduleMeasurement() }

        deinit {
            for observer in textObservers { NotificationCenter.default.removeObserver(observer) }
        }

        func connect(to controller: ComposerInputController) {
            guard inputController !== controller else { return }
            if let editorObserver { inputController?.removeEditorObserver(editorObserver) }
            inputController = controller
            editorObserver = controller.observeEditor { [weak self] editor in
                guard let self else { return }
                if let editor { self.attachEditor(editor) }
                else { self.detachEditor() }
                self.scheduleMeasurement()
            }
        }

        func tearDown() {
            if let editorObserver { inputController?.removeEditorObserver(editorObserver) }
            editorObserver = nil; inputController = nil
            detachEditor()
            onHeightChange = nil
        }

        private func detachEditor() {
            for observer in textObservers { NotificationCenter.default.removeObserver(observer) }
            textObservers.removeAll()
            placeholderLabel?.removeFromSuperview()
            placeholderLabel = nil
            editor = nil
        }

        /// SwiftUI's binding may still be empty while an IME owns marked text.
        /// Read the actual native storage for presentation, without committing
        /// composition or replacing the editor/delegate to update a placeholder.
        func synchronizePlaceholder() {
            guard let editor, let label = placeholderLabel else { return }
            label.stringValue = placeholder
            label.font = editor.font ?? .systemFont(ofSize: 13)
            label.isHidden = placeholder.isEmpty || !editor.string.isEmpty || editor.hasMarkedText()
            let origin = editor.textContainerOrigin
            let padding = editor.textContainer?.lineFragmentPadding ?? 0
            let lineHeight = editor.layoutManager?.defaultLineHeight(for: label.font!) ?? 17
            label.frame = NSRect(x: origin.x + padding, y: max(0, origin.y - 1), width: max(0, editor.bounds.width - origin.x - padding - 4), height: lineHeight + 2)
        }

        private func attachEditor(_ found: NSTextView) {
            guard editor !== found else { return }
            detachEditor()
            editor = found
            let label = ComposerPlaceholderLabel(labelWithString: placeholder)
            label.textColor = .placeholderTextColor
            label.isSelectable = false; label.isEditable = false
            label.lineBreakMode = .byTruncatingTail
            label.maximumNumberOfLines = 1
            label.setAccessibilityElement(false)
            found.addSubview(label)
            placeholderLabel = label
            let sources: [(Notification.Name, Any)] = [
                (NSText.didChangeNotification, found),
                (NSTextView.didChangeSelectionNotification, found),
            ] + (found.textStorage.map { [(NSTextStorage.didProcessEditingNotification, $0 as Any)] } ?? [])
            for (name, object) in sources {
                textObservers.append(NotificationCenter.default.addObserver(forName: name, object: object, queue: .main) { [weak self] _ in
                    self?.synchronizePlaceholder()
                    self?.scheduleMeasurement()
                })
            }
            synchronizePlaceholder()
        }

        func scheduleMeasurement() {
            guard !scheduled else { return }
            scheduled = true
            DispatchQueue.main.async { [weak self] in
                guard let self else { return }
                self.scheduled = false
                self.measure()
            }
        }

        private func measure() {
            guard window != nil, bounds.width > 1 else { return }
            if editor?.window !== window { detachEditor() }
            if editor == nil {
                guard let found = inputController?.editor, found.window === window else { return }
                attachEditor(found)
            }
            guard let editor, let sourceContainer = editor.textContainer else { return }
            synchronizePlaceholder()
            let width = sourceContainer.size.width
            guard width.isFinite, width > 1 else { return }
            let font = editor.font ?? NSFont.systemFont(ofSize: 13)
            let storage = NSTextStorage(attributedString: editor.attributedString())
            let layout = NSLayoutManager()
            let container = NSTextContainer(size: NSSize(width: width, height: .greatestFiniteMagnitude))
            container.lineFragmentPadding = sourceContainer.lineFragmentPadding
            container.lineBreakMode = sourceContainer.lineBreakMode
            storage.addLayoutManager(layout)
            layout.addTextContainer(container)
            let lineHeight = layout.defaultLineHeight(for: font)
            let maximumBody = lineHeight * 6
            // Only the first six lines are needed to choose the viewport height.
            layout.ensureLayout(forBoundingRect: NSRect(x: 0, y: 0, width: width, height: maximumBody + lineHeight), in: container)
            let used = max(layout.usedRect(for: container).maxY, layout.extraLineFragmentUsedRect.maxY)
            let verticalInsets = max(0, editor.textContainerOrigin.y) + editor.textContainerInset.height + 2
            onHeightChange?(ceil(min(maximumBody, max(lineHeight, used)) + verticalInsets))
        }


    }
}

/// Visual hint only: every pointer event stays with the original NSTextView.
final class ComposerPlaceholderLabel: NSTextField {
    override func hitTest(_ point: NSPoint) -> NSView? { nil }
}
