import AppKit

/// Exercises the actual host copy override without accessing any pasteboard,
/// creating a PTY, or changing the user's selected input source.
@MainActor
enum TerminalCopyDiagnostics {
    static func run() -> [String: Bool] {
        var selection: String? = nil
        var writes: [String] = []
        var acceptsWrite = true
        let view = HostTerminalView(
            frame: .zero,
            readSelectedText: { selection },
            writeSelectedText: { text in
                writes.append(text)
                return acceptsWrite
            }
        )
        var report: [String: Bool] = [:]
        report["terminalNoSelectionLeavesClipboardUntouched"] = !view.copySelectedTextToPasteboard() && writes.isEmpty
        selection = ""
        report["terminalEmptySelectionLeavesClipboardUntouched"] = !view.copySelectedTextToPasteboard() && writes.isEmpty

        selection = "한글과 English\n  들여쓰기 👋\n"
        report["terminalCopiesExactUnicodeSelectionOnce"] = view.copySelectedTextToPasteboard() && writes == [selection!]
        writes.removeAll()
        selection = "  \n\t"
        report["terminalCopiesWhitespaceSelection"] = view.copySelectedTextToPasteboard() && writes == [selection!]

        writes.removeAll()
        selection = "메뉴에서 복사"
        let item = view.selectionContextMenu().items.first
        if let action = item?.action {
            report["terminalContextMenuUsesHostCopy"] = NSApp.sendAction(action, to: item?.target, from: item) && writes == [selection!]
        } else {
            report["terminalContextMenuUsesHostCopy"] = false
        }

        writes.removeAll()
        acceptsWrite = false
        report["terminalClipboardFailureIsReported"] = !view.copySelectedTextToPasteboard() && writes.count == 1
        return report
    }
}
