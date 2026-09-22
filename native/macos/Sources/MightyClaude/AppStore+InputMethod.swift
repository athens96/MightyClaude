import AppKit
import Foundation

extension AppStore {
    /// The coordinator can retain a weak, validated target even when AppKit has
    /// lost the key window. Activation is verified before reconnecting it.
    func reconnectInputMethod(editor: NSTextView?) {
        InputMethodMonitor.shared.attemptRecovery(editor: editor)
        inputMethodProblem = InputMethodMonitor.shared.problem
    }

    func dismissInputMethodProblem() {
        InputMethodMonitor.shared.clearProblem()
        inputMethodProblem = nil
    }

    /// Manual capture for the next report: writes the snapshot and shows where.
    func saveInputMethodDiagnostics() {
        let editor = InputSessionRecoveryCoordinator.shared.resolveEditor()
        if let file = InputMethodMonitor.shared.writeDiagnostics(editor: editor, reason: "manual") {
            NSWorkspace.shared.selectFile(file.path, inFileViewerRootedAtPath: file.deletingLastPathComponent().path)
        } else { error = "진단 파일을 저장하지 못했습니다." }
    }
}
