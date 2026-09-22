import AppKit

/// Exercises the production monitor handler with synthetic window state and a
/// counting copy target. No pasteboard reads/writes or input-source changes.
@MainActor
enum ApplicationCopyDiagnostics {
    static func run() -> [String: Bool] {
        _ = NSApplication.shared
        var keyboardInteractions = 0
        let router = ApplicationCopyRouter(beforeAction: { _ in keyboardInteractions += 1 })
        let window = FixtureWindow(contentRect: NSRect(x: 0, y: 0, width: 400, height: 200),
                                   styleMask: [.titled], backing: .buffered, defer: false)
        let otherWindow = NSWindow(contentRect: .zero, styleMask: [], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        otherWindow.isReleasedWhenClosed = false
        defer { window.close(); otherWindow.close() }
        let root = NSView(frame: NSRect(x: 0, y: 0, width: 400, height: 200))
        let block = CountingBlock(frame: root.bounds)
        block.isEditable = false
        block.isSelectable = true
        block.string = "한글 블록 복사"
        root.addSubview(block)
        window.contentView = root
        block.setSelectedRange(NSRange(location: 0, length: (block.string as NSString).length))
        block.claimSelectionFocus()
        let event = FixtureEvent()
        event.fixtureWindow = window
        var report: [String: Bool] = [:]
        let initial = block.copyCalls
        report["focusedKoreanCopyBeforeMenu"] = router.handle(event, keyWindow: window, modalWindow: nil) == nil
            && block.copyCalls == initial + 1 && block.copyWrites == 1
        report["nilKeyWindowStillRoutesFocusedEvent"] = router.handle(event, keyWindow: nil, modalWindow: nil) == nil
            && block.copyCalls == initial + 2
        event.flags = [.command, .capsLock, .function]
        report["incidentalFlagsDoNotBlockCopy"] = router.handle(event, keyWindow: nil, modalWindow: nil) == nil
        event.flags = [.command, .shift]
        let beforeModifiers = block.copyCalls
        report["modifiedShortcutPassesThrough"] = router.handle(event, keyWindow: nil, modalWindow: nil) === event
            && block.copyCalls == beforeModifiers
        event.flags = .command
        let beforeGuards = block.copyCalls
        report["differentKeyWindowPassesThrough"] = router.handle(event, keyWindow: otherWindow, modalWindow: nil) === event
        report["modalWindowPassesThrough"] = router.handle(event, keyWindow: nil, modalWindow: otherWindow) === event
        window.fixtureSheet = otherWindow
        report["attachedSheetPassesThrough"] = router.handle(event, keyWindow: nil, modalWindow: nil) === event
        window.fixtureSheet = nil
        window.fixtureVisible = false
        report["hiddenWindowPassesThrough"] = router.handle(event, keyWindow: nil, modalWindow: nil) === event
        window.fixtureVisible = true
        window.fixtureMiniaturized = true
        report["minimizedWindowPassesThrough"] = router.handle(event, keyWindow: nil, modalWindow: nil) === event
        window.fixtureMiniaturized = false
        event.fixtureWindow = otherWindow
        report["foreignEventDoesNotCopyBlock"] = router.handle(event, keyWindow: nil, modalWindow: nil) === event
        event.fixtureWindow = window
        report["guardsNeverCopy"] = block.copyCalls == beforeGuards
        block.isHidden = true
        report["hiddenBlockPassesThrough"] = router.handle(event, keyWindow: nil, modalWindow: nil) === event
        block.isHidden = false
        block.setSelectedRange(NSRange(location: 0, length: 0))
        let beforeEmpty = block.copyWrites
        // Hiding the previous fixture resigned first responder; revealing it
        // does not restore focus. Explicitly establish this case's precondition.
        report["focusedEmptySelectionConsumesWithoutWriting"] = window.makeFirstResponder(block)
            && router.handle(event, keyWindow: nil, modalWindow: nil) == nil
            && block.copyWrites == beforeEmpty
        block.setSelectedRange(NSRange(location: 0, length: (block.string as NSString).length))
        block.claimSelectionFocus()
        let editor = CountingEditor(frame: NSRect(x: 0, y: 0, width: 80, height: 30))
        root.addSubview(editor)
        let beforeOther = block.copyCalls
        report["otherEditorKeepsCopyOwnership"] = window.makeFirstResponder(editor)
            && router.handle(event, keyWindow: nil, modalWindow: nil) == nil
            && editor.copyCalls == 1 && block.copyCalls == beforeOther
        event.code = 9; event.text = "ㅍ"
        report["focusedKoreanPasteUsesNativeActionOnce"] = router.handle(event, keyWindow: nil, modalWindow: nil) == nil
            && editor.pasteCalls == 1
        let beforeModalPaste = editor.pasteCalls
        report["ownModalEditorReceivesNativePaste"] = router.handle(event, keyWindow: window, modalWindow: window) == nil
            && editor.pasteCalls == beforeModalPaste + 1
        let beforeBlockedPaste = editor.pasteCalls
        report["otherModalBlocksNativePaste"] = router.handle(event, keyWindow: nil, modalWindow: otherWindow) === event
            && editor.pasteCalls == beforeBlockedPaste
        editor.isEditable = false
        report["readonlyPasteNeverTargetsBackgroundSelection"] = router.handle(event, keyWindow: nil, modalWindow: nil) === event
            && editor.pasteCalls == beforeBlockedPaste && block.copyCalls == beforeOther
        editor.isEditable = true
        event.flags = [.command, .shift]
        report["pasteAndMatchStyleKeepsNativePath"] = router.handle(event, keyWindow: nil, modalWindow: nil) === event
            && editor.pasteCalls == beforeBlockedPaste
        event.flags = .command
        event.code = 8; event.text = "v"
        report["latinPasteOnPhysicalCopyKeyRemainsPaste"] = router.handle(event, keyWindow: nil, modalWindow: nil) == nil
            && editor.pasteCalls == beforeBlockedPaste + 1 && editor.copyCalls == 1
        event.code = 9; event.text = "c"
        report["latinCopyOnPhysicalPasteKeyRemainsCopy"] = router.handle(event, keyWindow: nil, modalWindow: nil) == nil
            && editor.copyCalls == 2
        event.text = "j"
        report["otherLatinLayoutShortcutPassesThrough"] = router.handle(event, keyWindow: nil, modalWindow: nil) === event
            && editor.copyCalls == 2 && editor.pasteCalls == beforeBlockedPaste + 1
        event.code = 9; event.text = "ㅍ"
        editor.setMarkedText("한", selectedRange: NSRange(location: 1, length: 0), replacementRange: NSRange(location: 0, length: 0))
        let selectedBeforePaste = editor.selectedRanges
        report["routingDoesNotCommitOrDiscardMarkedText"] = router.handle(event, keyWindow: nil, modalWindow: nil) == nil
            && editor.hasMarkedText() && editor.string == "한" && editor.selectedRanges == selectedBeforePaste
        editor.unmarkText()
        editor.isHidden = true
        let hiddenPasteCalls = editor.pasteCalls
        report["hiddenEditorPasteCannotFallBack"] = router.handle(event, keyWindow: nil, modalWindow: nil) === event
            && editor.pasteCalls == hiddenPasteCalls
        editor.isHidden = false
        let field = FixtureSecureField(frame: NSRect(x: 100, y: 0, width: 100, height: 30))
        root.addSubview(field)
        field.fixtureEditor = editor
        editor.isFieldEditor = true
        editor.delegate = field
        window.makeFirstResponder(editor)
        event.code = 8; event.text = "ㅊ"
        let beforeSecureCopy = editor.copyCalls
        report["secureFieldDoesNotGainCopyAction"] = router.handle(event, keyWindow: window, modalWindow: nil) == nil
            && editor.copyCalls == beforeSecureCopy
        event.code = 9; event.text = "ㅍ"
        let beforeSecurePaste = editor.pasteCalls
        report["secureFieldKeepsNativePaste"] = router.handle(event, keyWindow: window, modalWindow: nil) == nil
            && editor.pasteCalls == beforeSecurePaste + 1
        field.isEnabled = false
        report["disabledFieldEditorCannotPaste"] = router.handle(event, keyWindow: window, modalWindow: nil) === event
            && editor.pasteCalls == beforeSecurePaste + 1
        field.isEnabled = true
        field.fixtureEditor = nil
        report["staleSharedFieldEditorCannotPaste"] = router.handle(event, keyWindow: window, modalWindow: nil) === event
            && editor.pasteCalls == beforeSecurePaste + 1
        editor.delegate = nil
        report["ownerlessSharedFieldEditorCannotPaste"] = router.handle(event, keyWindow: window, modalWindow: nil) === event
            && editor.pasteCalls == beforeSecurePaste + 1
        editor.isFieldEditor = false
        field.removeFromSuperview()
        // Ending a native field-editor session detaches its shared editor.
        // Re-mount it before testing unrelated callback reentrancy.
        root.addSubview(editor)
        let replacement = CountingEditor(frame: editor.frame)
        root.addSubview(replacement)
        let beforeReentrantPaste = editor.pasteCalls
        let changingRouter = ApplicationCopyRouter(beforeAction: { _ in window.makeFirstResponder(replacement) })
        report["cancellationCallbackCannotPasteIntoStaleOrNewEditor"] = window.makeFirstResponder(editor)
            && changingRouter.handle(event, keyWindow: window, modalWindow: nil) == nil
            && editor.pasteCalls == beforeReentrantPaste && replacement.pasteCalls == 0
        let removingRouter = ApplicationCopyRouter(beforeAction: { _ in editor.removeFromSuperview() })
        report["cancellationCallbackUnmountCancelsPaste"] = window.makeFirstResponder(editor)
            && removingRouter.handle(event, keyWindow: window, modalWindow: nil) == nil
            && editor.pasteCalls == beforeReentrantPaste && replacement.pasteCalls == 0
        root.addSubview(editor)
        replacement.removeFromSuperview()
        event.code = 8; event.text = "ㅊ"
        let canvas = Canvas(frame: root.bounds)
        root.addSubview(canvas)
        report["canvasFocusUsesVisibleRecentSelection"] = window.makeFirstResponder(canvas)
            && router.handle(event, keyWindow: nil, modalWindow: nil) == nil && block.copyCalls == beforeOther + 1
        var terminalSelection: String? = "터미널 한글 선택"
        var terminalWrites: [String] = []
        let terminal = HostTerminalView(frame: root.bounds, readSelectedText: { terminalSelection },
                                        writeSelectedText: { terminalWrites.append($0); return true })
        root.addSubview(terminal)
        let beforeTerminal = block.copyCalls
        report["terminalKoreanCopyUsesExplicitSelection"] = window.makeFirstResponder(terminal)
            && router.handle(event, keyWindow: nil, modalWindow: nil) == nil
            && terminalWrites == ["터미널 한글 선택"] && block.copyCalls == beforeTerminal
        terminalSelection = nil
        report["terminalEmptySelectionConsumesWithoutWriting"] = router.handle(event, keyWindow: nil, modalWindow: nil) == nil
            && terminalWrites.count == 1 && block.copyCalls == beforeTerminal
        // Check the exact terminal selector and target without executing
        // Ghostty's action, which reads the user's general pasteboard.
        var terminalPasteDispatches = 0
        var interactionBeforeDispatch = false
        var notified = false
        let terminalRouter = ApplicationCopyRouter(beforeAction: { received in
            notified = received === window
        }, sendAction: { action, target in
            guard action == #selector(NSText.paste(_:)), target === terminal else { return false }
            interactionBeforeDispatch = notified
            terminalPasteDispatches += 1
            return true
        })
        event.code = 9; event.text = "ㅍ"
        report["terminalKoreanPasteDispatchesExistingNativeActionOnce"] = terminal.responds(to: #selector(NSText.paste(_:)))
            && terminalRouter.handle(event, keyWindow: window, modalWindow: nil) == nil
            && terminalPasteDispatches == 1 && interactionBeforeDispatch
        event.code = 8; event.text = "ㅊ"
        report["consumedClipboardNotifiesLifecycle"] = keyboardInteractions > 0
        // Run queued-focus decisions directly with synthetic app/key state. No
        // window activation, terminal surface, or process is needed.
        func focusRequest() -> TerminalFocusRequest? {
            guard window.makeFirstResponder(editor) else { return nil }
            return TerminalFocusRequest(view: terminal, allowReplacingResponder: true)
        }
        report["terminalPassiveReadinessPreservesExistingEditor"] = window.makeFirstResponder(editor)
            && TerminalFocusRequest(view: terminal) == nil && window.firstResponder === editor
        let plainFocused = window.makeFirstResponder(canvas)
        let initialFocus = TerminalFocusRequest(view: terminal)
        report["terminalInitialFocusAcceptsPlainResponder"] = plainFocused
            && initialFocus?.perform(appActive: true, keyWindow: window, modalWindow: nil) == true
            && window.firstResponder === terminal
        let validFocus = focusRequest()
        report["terminalExplicitSessionFocusSucceedsWhenOwnershipUnchanged"] = validFocus?.perform(appActive: true, keyWindow: window, modalWindow: nil) == true
            && window.firstResponder === terminal
        let supersededFocus = focusRequest()
        report["terminalQueuedFocusPreservesNewerEditor"] = window.makeFirstResponder(block)
            && supersededFocus?.perform(appActive: true, keyWindow: window, modalWindow: nil) == false
            && window.firstResponder === block
        let guardedFocus = focusRequest()
        report["terminalQueuedFocusSkipsInactiveApp"] = guardedFocus?.perform(appActive: false, keyWindow: window, modalWindow: nil) == false
            && window.firstResponder === editor
        report["terminalQueuedFocusSkipsOtherKeyWindow"] = guardedFocus?.perform(appActive: true, keyWindow: otherWindow, modalWindow: nil) == false
        window.fixtureKey = false
        report["terminalQueuedFocusSkipsNonkeyWindow"] = guardedFocus?.perform(appActive: true, keyWindow: window, modalWindow: nil) == false
        window.fixtureKey = true
        window.fixtureSheet = otherWindow
        report["terminalQueuedFocusSkipsSheet"] = guardedFocus?.perform(appActive: true, keyWindow: window, modalWindow: nil) == false
        window.fixtureSheet = nil
        report["terminalQueuedFocusSkipsModal"] = guardedFocus?.perform(appActive: true, keyWindow: window, modalWindow: otherWindow) == false
        let otherParent = NSView(frame: root.bounds)
        root.addSubview(otherParent)
        terminal.removeFromSuperview()
        otherParent.addSubview(terminal)
        report["terminalQueuedFocusSkipsRemountedPresentation"] = guardedFocus?.perform(appActive: true, keyWindow: window, modalWindow: nil) == false
        terminal.removeFromSuperview()
        root.addSubview(terminal)
        let movedFocus = focusRequest()
        otherWindow.contentView = NSView(frame: root.bounds)
        terminal.removeFromSuperview()
        otherWindow.contentView?.addSubview(terminal)
        report["terminalQueuedFocusSkipsMovedWindow"] = movedFocus?.perform(appActive: true, keyWindow: window, modalWindow: nil) == false
        terminal.removeFromSuperview()
        root.addSubview(terminal)
        report["terminalFocusWaitsForMount"] = TerminalFocusRequest(view: Canvas(frame: root.bounds)) == nil
        return report
    }

    private final class FixtureWindow: NSWindow {
        var fixtureVisible = true
        var fixtureKey = true
        var fixtureMiniaturized = false
        var fixtureSheet: NSWindow?
        override var isVisible: Bool { fixtureVisible }
        override var isKeyWindow: Bool { fixtureKey }
        override var isMiniaturized: Bool { fixtureMiniaturized }
        override var attachedSheet: NSWindow? { fixtureSheet }
    }

    private final class FixtureEvent: NSEvent {
        var fixtureWindow: NSWindow?
        var flags: NSEvent.ModifierFlags = .command
        var code: UInt16 = 8
        var text: String? = "ㅊ"
        override var window: NSWindow? { fixtureWindow }
        override var type: NSEvent.EventType { .keyDown }
        override var modifierFlags: NSEvent.ModifierFlags { flags }
        override var keyCode: UInt16 { code }
        override var charactersIgnoringModifiers: String? { text }
    }

    private final class CountingEditor: NSTextView {
        var copyCalls = 0
        var pasteCalls = 0
        override func copy(_ sender: Any?) { copyCalls += 1 }
        override func paste(_ sender: Any?) { pasteCalls += 1 }
    }

    private final class FixtureSecureField: NSSecureTextField, NSTextViewDelegate {
        weak var fixtureEditor: NSText?
        override func currentEditor() -> NSText? { fixtureEditor }
    }

    private final class CountingBlock: SelectableTextView {
        var copyCalls = 0
        var copyWrites = 0
        override func copy(_ sender: Any?) {
            copyCalls += 1
            if selectionLength > 0 { copyWrites += 1 }
        }
    }

    private final class Canvas: NSView {
        override var acceptsFirstResponder: Bool { true }
    }
}
