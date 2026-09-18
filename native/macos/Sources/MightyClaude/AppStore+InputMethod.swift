import AppKit
import Foundation

extension AppStore {
    /// Tries to re-establish the composition session in the focused editor
    /// (or the key window's first responder) without relaunching.
    func reconnectInputMethod(editor: NSTextView?) {
        let target = editor ?? (NSApp.keyWindow?.firstResponder as? NSTextView)
        InputMethodMonitor.shared.attemptRecovery(editor: target)
        inputMethodProblem = InputMethodMonitor.shared.problem
    }

    func dismissInputMethodProblem() {
        InputMethodMonitor.shared.clearProblem()
        inputMethodProblem = nil
    }

    /// Manual capture for the next report: writes the snapshot and shows where.
    func saveInputMethodDiagnostics() {
        let editor = NSApp.keyWindow?.firstResponder as? NSTextView
        if let file = InputMethodMonitor.shared.writeDiagnostics(editor: editor, reason: "manual") {
            NSWorkspace.shared.selectFile(file.path, inFileViewerRootedAtPath: file.deletingLastPathComponent().path)
        } else { error = "진단 파일을 저장하지 못했습니다." }
    }
}
