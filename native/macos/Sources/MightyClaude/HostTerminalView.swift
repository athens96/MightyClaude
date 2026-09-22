import AppKit
import GhosttyTerminal

/// Copies an explicit user selection without routing it through Ghostty's
/// program-initiated clipboard-write confirmation. OSC 52 keeps its policy.
@MainActor
final class HostTerminalView: AppTerminalView {
    // The session supplies its lifecycle-owned surface through public APIs.
    // AppTerminalView's own surface property is internal to GhosttyTerminal.
    var readSelectedText: () -> String?
    private let writeSelectedText: (String) -> Bool

    init(
        frame: NSRect,
        readSelectedText: @escaping () -> String? = { nil },
        writeSelectedText: @escaping (String) -> Bool = { text in
            let pasteboard = NSPasteboard.general
            pasteboard.clearContents()
            return pasteboard.setString(text, forType: .string)
        }
    ) {
        self.readSelectedText = readSelectedText
        self.writeSelectedText = writeSelectedText
        super.init(frame: frame)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }

    @discardableResult
    override func copySelectedTextToPasteboard() -> Bool {
        guard let text = readSelectedText(), !text.isEmpty else { return false }
        return writeSelectedText(text)
    }
}
