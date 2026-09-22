import AppKit
import Foundation

/// Overrides touch only this process's named pasteboard, never the general one.
@MainActor final class CountingEditor: NSTextView {
    let board: NSPasteboard
    var copies = 0
    var pastes = 0
    var copyWriteSucceeded = false
    var pasteReadSucceeded = false
    init(board: NSPasteboard) {
        self.board = board
        let storage = NSTextStorage()
        let layout = NSLayoutManager()
        let container = NSTextContainer(size: NSSize(width: 300, height: 100))
        storage.addLayoutManager(layout); layout.addTextContainer(container)
        super.init(frame: NSRect(x: 0, y: 0, width: 300, height: 100), textContainer: container)
        isEditable = true; isSelectable = true; isRichText = false
    }
    required init?(coder: NSCoder) { fatalError("fixture does not decode") }
    override func copy(_ sender: Any?) {
        copies += 1
        board.clearContents()
        let range = selectedRange()
        copyWriteSucceeded = board.setString((string as NSString).substring(with: range), forType: .string)
    }
    override func paste(_ sender: Any?) {
        pastes += 1
        if let text = board.string(forType: .string) { pasteReadSucceeded = true; insertText(text, replacementRange: selectedRange()) }
    }
}

@main struct NativeMenuShortcutProbe {
    @MainActor static func main() throws {
        let app = NSApplication.shared
        let originalActive = app.isActive
        let originalKey = app.keyWindow
        let originalMainMenu = app.mainMenu
        let board = NSPasteboard.withUniqueName()
        defer { board.releaseGlobally(); app.mainMenu = originalMainMenu }
        let clipboardAvailable = board.setString("private baseline", forType: .string) && board.string(forType: .string) == "private baseline"
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 300, height: 100), styleMask: .titled, backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        defer { window.contentView = nil; window.close() }
        let editor = CountingEditor(board: board)
        window.contentView = editor
        let ownsResponder = window.makeFirstResponder(editor) && window.firstResponder === editor
        let menu = NSMenu(title: "Private fixture menu")
        menu.autoenablesItems = false
        let copy = NSMenuItem(title: "Copy fixture", action: #selector(NSText.copy(_:)), keyEquivalent: "c")
        let paste = NSMenuItem(title: "Paste fixture", action: #selector(NSText.paste(_:)), keyEquivalent: "v")
        for item in [copy, paste] {
            // Explicit target isolates native matching and action delivery from
            // key-window discovery, which cannot be real without activation.
            item.target = window.firstResponder
            item.keyEquivalentModifierMask = .command
            item.isEnabled = true
            menu.addItem(item)
        }
        app.mainMenu = menu
        var trials: [[String: Any]] = []
        let cases: [(String, String, UInt16, NSEvent.ModifierFlags)] = [
            ("latin-copy", "c", 8, .command),
            ("uppercase-copy", "C", 8, .command),
            ("korean-copy", "ㅊ", 8, .command),
            ("latin-paste", "v", 9, .command),
            ("uppercase-paste", "V", 9, .command),
            ("korean-paste", "ㅍ", 9, .command),
            ("copy-character-paste-keycode", "c", 9, .command),
            ("paste-character-copy-keycode", "v", 8, .command),
            ("shift-copy", "C", 8, [.command, .shift]),
            ("plain-copy", "c", 8, [])
        ]
        for (name, characters, code, modifiers) in cases {
            editor.string = "named pasteboard fixture 한글"
            editor.setSelectedRange(NSRange(location: 0, length: (editor.string as NSString).length))
            editor.copies = 0; editor.pastes = 0; editor.copyWriteSucceeded = false; editor.pasteReadSucceeded = false
            board.clearContents(); _ = board.setString("private paste payload", forType: .string)
            guard let event = NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: modifiers,
                timestamp: ProcessInfo.processInfo.systemUptime, windowNumber: window.windowNumber, context: nil,
                characters: characters, charactersIgnoringModifiers: characters, isARepeat: false, keyCode: code) else { fatalError("event fixture construction failed") }
            let matched = menu.performKeyEquivalent(with: event)
            trials.append(["case": name, "physicalKeyCode": code, "characters": characters,
                           "nativeMenuMatched": matched, "copyActions": editor.copies, "pasteActions": editor.pastes,
                           "privateBoardValue": board.string(forType: .string) ?? "nil", "fixtureNativeString": editor.string, "selectedRange": NSStringFromRange(editor.selectedRange()),
                           "copyWriteSucceeded": editor.copyWriteSucceeded, "pasteReadSucceeded": editor.pasteReadSucceeded,
                           "copyPayloadVerified": clipboardAvailable && editor.copies == 1 && board.string(forType: .string) == "named pasteboard fixture 한글",
                           "pastePayloadVerified": clipboardAvailable && editor.pastes == 1 && editor.string == "private paste payload"])
        }
        let latinControls = trials.filter { ["latin-copy", "latin-paste"].contains($0["case"] as? String ?? "") }
        let payloadControlsPassed = clipboardAvailable && latinControls.count == 2 && latinControls.allSatisfy {
            ($0["copyPayloadVerified"] as? Bool == true) || ($0["pastePayloadVerified"] as? Bool == true)
        }
        let report: [String: Any] = ["baselinePayloadControlsPassed": payloadControlsPassed,"trials": trials, "namedPasteboardAvailable": clipboardAvailable,
            "offscreenEditorIsFirstResponder": ownsResponder, "targetMode": "explicit offscreen firstResponder",
            "activationUnchanged": app.isActive == originalActive, "keyWindowUnchanged": app.keyWindow === originalKey,
            "windowNeverVisible": !window.isVisible, "generalClipboardAccessed": false,
            "inputSourceChanged": false, "globalEventsPosted": false,
            "limitation": "Literal NSEvent character fixtures isolate menu matching; they do not prove what physical Korean Command keys normally deliver."]
        print(String(decoding: try JSONSerialization.data(withJSONObject: report, options: [.prettyPrinted, .sortedKeys]), as: UTF8.self))
        guard ownsResponder, !window.isVisible, app.isActive == originalActive, app.keyWindow === originalKey else { exit(1) }
    }
}
