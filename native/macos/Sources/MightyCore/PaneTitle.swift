import Foundation

/// Pure, stateless helper for automatic pane-title generation from user requests.
public enum PaneTitle {
    /// Returns a shortened one-line title for the given raw input text, or nil when
    /// the input is empty or whitespace-only (leaving the previous title unchanged).
    ///
    /// Rule: collapse newlines and whitespace runs into a single space, trim,
    /// take the first 40 characters (Character count), append "…" when longer.
    /// Slash-command inputs ("/" prefix) follow the same rule.
    public static func shortened(_ rawInput: String) -> String? {
        let collapsed = rawInput
            .components(separatedBy: .whitespacesAndNewlines)
            .filter { !$0.isEmpty }
            .joined(separator: " ")
        guard !collapsed.isEmpty else { return nil }
        guard collapsed.count > 40 else { return collapsed }
        return String(collapsed.prefix(40)) + "…"
    }

    /// Returns the automatic title for a session: the shortened most-recent titled
    /// user log entry, or `defaultTitle` when none exists.
    public static func autoTitle(for session: RunSession, defaultTitle: String) -> String {
        guard let text = session.titleTooltip else { return defaultTitle }
        return shortened(text) ?? defaultTitle
    }
}
