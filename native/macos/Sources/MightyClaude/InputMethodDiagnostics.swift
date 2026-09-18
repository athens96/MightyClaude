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
    struct Problem: Equatable { let detectedAt: Date; let file: URL?; var recoveryAttempts = 0 }

    private(set) var events: [Event] = []
    private var detector = InputMethodSymptom.Detector()
    private var composedThisKey = false
    private var keyDepth = 0
    var dataDirectory: URL?
    var onProblem: ((Problem) -> Void)?
    private(set) var problem: Problem?
    private let launchedAt = Date()

    func record(_ text: String) {
        events.append(Event(time: Date().timeIntervalSince1970, text: text))
        if events.count > 400 { events.removeFirst(events.count - 400) }
    }

    func keyBegan(_ event: NSEvent) {
        keyDepth += 1
        if keyDepth == 1 { composedThisKey = false }
        record("keyDown code=\(event.keyCode) chars=\(event.characters.map { String($0.unicodeScalars.map { String(format: "U+%04X", $0.value) }.joined(separator: " ")) } ?? "-")")
    }
    func keyEnded() { keyDepth = max(0, keyDepth - 1) }

    func noteMarkedText(_ string: Any, selectedRange: NSRange, in editor: NSTextView) {
        composedThisKey = true
        let text = (string as? String) ?? (string as? NSAttributedString)?.string ?? "?"
        record("setMarkedText \(Self.codes(text)) sel=\(selectedRange) marked=\(editor.markedRange())")
    }
    func noteUnmark(in editor: NSTextView) { record("unmarkText hadMarked=\(editor.hasMarkedText())") }

    func noteInsert(_ string: Any, replacementRange: NSRange, in editor: NSTextView) {
        let text = (string as? String) ?? (string as? NSAttributedString)?.string ?? ""
        let source = Self.currentInputSourceID()
        let replaced = replacementRange.location == NSNotFound ? "none" : NSStringFromRange(replacementRange)
        record("insertText \(Self.codes(text)) replace=\(replaced) marked=\(composedThisKey) source=\(source ?? "-")")
        let koreanSource = InputMethodSymptom.isKoreanInputSource(source)
        let caret = min(editor.selectedRange().location, (editor.string as NSString).length)
        let before = (editor.string as NSString).substring(with: NSRange(location: max(0, caret - 2), length: min(2, caret)))
        if detector.observeInsert(textBeforeCaret: before, koreanSource: koreanSource, at: Date().timeIntervalSince1970), problem == nil {
            let file = writeDiagnostics(editor: editor, reason: "consonant and vowel jamo left uncombined twice (input method stopped replacing)")
            problem = Problem(detectedAt: Date(), file: file)
            onProblem?(problem!)
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
            "keyWindow": NSApp.keyWindow?.title ?? "-",
            "mainWindow": NSApp.mainWindow?.title ?? "-",
            "firstResponder": NSApp.keyWindow?.firstResponder.map { String(describing: type(of: $0)) } ?? "-",
            "secureEventInput": IsSecureEventInputEnabled(),
            "currentInputSource": Self.currentInputSourceID() ?? "-",
            "keyboardLayoutSource": Self.currentLayoutSourceID() ?? "-",
            "textInputContextCurrent": NSTextInputContext.current.map { String(describing: ObjectIdentifier($0)) } ?? "-",
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
        }
        info["recentEvents"] = events.suffix(120).map { String(format: "%.3f ", $0.time) + $0.text }
        return info
    }

    @discardableResult
    func writeDiagnostics(editor: NSTextView?, reason: String) -> URL? {
        guard let directory = dataDirectory?.appendingPathComponent("diagnostics", isDirectory: true) else { return nil }
        var info = snapshot(editor: editor); info["reason"] = reason
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let stamp = ISO8601DateFormatter().string(from: Date()).replacingOccurrences(of: ":", with: "")
        let file = directory.appendingPathComponent("ime-\(stamp).json")
        guard JSONSerialization.isValidJSONObject(info), let data = try? JSONSerialization.data(withJSONObject: info, options: [.prettyPrinted, .sortedKeys]) else { return nil }
        try? data.write(to: file)
        return file
    }

    /// Tries to re-establish the composition session without relaunching:
    /// drop marked text, re-activate the editor's input context and first
    /// responder, then flip the input source to ABC and back to the current one.
    func attemptRecovery(editor: NSTextView?) {
        record("recovery attempt")
        problem?.recoveryAttempts += 1
        if let editor {
            editor.inputContext?.discardMarkedText()
            editor.inputContext?.invalidateCharacterCoordinates()
            editor.inputContext?.deactivate()
            editor.window?.makeFirstResponder(nil)
            editor.window?.makeFirstResponder(editor)
            editor.inputContext?.activate()
        }
        if let current = TISCopyCurrentKeyboardInputSource()?.takeRetainedValue(), let ascii = TISCopyCurrentASCIICapableKeyboardInputSource()?.takeRetainedValue() {
            TISSelectInputSource(ascii)
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.15) { TISSelectInputSource(current) }
        }
        detector.reset()
    }

    func clearProblem() { problem = nil; detector.reset() }

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
