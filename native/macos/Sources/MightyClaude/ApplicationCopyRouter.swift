import AppKit
import MightyCore

/// Resolves clipboard shortcuts before character-dependent menu matching, then
/// invokes the focused view's native action, including its paste overrides.
@MainActor
final class ApplicationCopyRouter {
    private var monitor: Any?
    private let beforeAction: @MainActor (NSWindow) -> Void
    private let sendAction: @MainActor (Selector, NSResponder) -> Bool

    init(
        beforeAction: @escaping @MainActor (NSWindow) -> Void = { InputSessionRecoveryCoordinator.shared.keyboardInteraction(in: $0) },
        sendAction: @escaping @MainActor (Selector, NSResponder) -> Bool = { NSApp.sendAction($0, to: $1, from: nil) }
    ) {
        self.beforeAction = beforeAction
        self.sendAction = sendAction
    }

    func install() {
        guard monitor == nil else { return }
        monitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] event in
            guard let self else { return event }
            return self.handle(event, keyWindow: NSApp.keyWindow, modalWindow: NSApp.modalWindow)
        }
    }

    func uninstall() {
        if let monitor { NSEvent.removeMonitor(monitor) }
        monitor = nil
    }

    /// Explicit window context also lets diagnostics exercise this exact handler
    /// without making a test window key or touching the user's clipboard.
    func handle(_ event: NSEvent, keyWindow: NSWindow?, modalWindow: NSWindow?) -> NSEvent? {
        guard event.type == .keyDown,
              let shortcut = ClipboardShortcut.match(modifiers: event.modifierFlags.rawValue,
                                                      characters: event.charactersIgnoringModifiers,
                                                      keyCode: event.keyCode),
              let window = event.window, window.isVisible, !window.isMiniaturized,
              window.attachedSheet == nil, modalWindow == nil || modalWindow === window,
              keyWindow == nil || keyWindow === window else { return event }
        if let target = window.firstResponder as? NSView,
           target is NSTextView || target is HostTerminalView {
            guard target.window === window, !target.isHiddenOrHasHiddenAncestor,
                  !target.visibleRect.isEmpty else { return event }
            if let editor = target as? NSTextView {
                // Shared field editors must still belong to the visible,
                // enabled control, not a previously edited hidden field.
                if editor.isFieldEditor {
                    guard let control = editor.delegate as? NSControl,
                          control.window === window, control.currentEditor() === editor,
                          control.isEnabled, !control.isHiddenOrHasHiddenAncestor,
                          !control.visibleRect.isEmpty else { return event }
                    if shortcut == .copy, control is NSSecureTextField {
                        beforeAction(window)
                        return nil
                    }
                }
                guard shortcut == .copy ? editor.isSelectable : editor.isEditable else { return event }
            }
            let action = shortcut == .copy ? #selector(NSText.copy(_:)) : #selector(NSText.paste(_:))
            guard target.responds(to: action) else { return event }
            let parent = target.superview
            let fieldOwner = (target as? NSTextView)?.delegate as? NSControl
            // This monitor can consume the event before the passive lifecycle
            // monitor sees it. Cancel a stale missed-click intent first.
            beforeAction(window)
            // Cancellation callbacks can synchronously change presentation or
            // focus. Never apply the old shortcut to a new owner.
            guard window.firstResponder === target, target.window === window,
                  target.superview === parent, window.isVisible, !window.isMiniaturized,
                  window.attachedSheet == nil, !target.isHiddenOrHasHiddenAncestor,
                  !target.visibleRect.isEmpty else { return nil }
            if let editor = target as? NSTextView {
                guard shortcut == .copy ? editor.isSelectable : editor.isEditable else { return nil }
                if editor.isFieldEditor {
                    guard let owner = fieldOwner, editor.delegate === owner, owner.window === window,
                          owner.currentEditor() === editor, owner.isEnabled,
                          !owner.isHiddenOrHasHiddenAncestor, !owner.visibleRect.isEmpty else { return nil }
                }
            }
            return sendAction(action, target) ? nil : event
        }
        if shortcut == .copy {
            // Only Copy can reclaim a visible transcript selection from the
            // canvas. Paste never falls through to another editor.
            let responder = window.firstResponder
            beforeAction(window)
            guard window.firstResponder === responder, window.isVisible, !window.isMiniaturized,
                  window.attachedSheet == nil else { return nil }
            if SelectableTextView.copyMostRecentSelection(for: event) { return nil }
        }
        return event
    }
}
