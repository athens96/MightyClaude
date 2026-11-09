import Foundation

/// A remark the user left on one line of a request's diff in the change
/// review, to be sent back to the agent as a follow-up request.
public struct ReviewNote: Equatable, Sendable, Identifiable {
    public enum Side: String, Sendable { case old, new }
    public let id: UUID
    public let path: String
    /// The line on its side: the file now (`new`) or before the request (`old`, a removed line).
    public let line: Int
    public let side: Side
    /// The line's text as the diff showed it.
    public let quote: String
    public var text: String

    public init(id: UUID = UUID(), path: String, line: Int, side: Side, quote: String, text: String) {
        self.id = id; self.path = path; self.line = line; self.side = side; self.quote = quote; self.text = text
    }
}

/// The follow-up request a review's notes become: a header line, then one
/// bullet per note in file and line order, each naming the place as the agent
/// can open it (`path:line`; a removed line by its number before the request).
public enum ReviewNotes {
    /// Quoted lines longer than this are cut with an ellipsis.
    public static let maximumQuote = 120

    /// `removedLabel` words a removed line's place, `{line}` its number before the request.
    public static func prompt(_ notes: [ReviewNote], header: String, removedLabel: String) -> String {
        let kept = notes.filter { !$0.text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
        guard !kept.isEmpty else { return "" }
        let sorted = kept.enumerated().sorted { a, b in
            (a.element.path, a.element.line, a.offset) < (b.element.path, b.element.line, b.offset)
        }.map(\.element)
        var lines = [header]
        for note in sorted {
            let place = note.side == .new ? "`\(note.path):\(note.line)`"
                : "`\(note.path)` (" + removedLabel.replacingOccurrences(of: "{line}", with: "\(note.line)") + ")"
            let quote = note.quote.trimmingCharacters(in: .whitespaces)
            let shown = quote.count > maximumQuote ? String(quote.prefix(maximumQuote)) + "…" : quote
            let body = note.text.trimmingCharacters(in: .whitespacesAndNewlines).replacingOccurrences(of: "\n", with: "\n  ")
            lines.append("- \(place)" + (shown.isEmpty ? "" : " — `\(shown.replacingOccurrences(of: "`", with: "'"))`") + "\n  \(body)")
        }
        return lines.joined(separator: "\n")
    }

    /// `block` after what the composer already holds, a blank line between.
    public static func appending(_ block: String, to draft: String) -> String {
        guard !block.isEmpty else { return draft }
        let trimmed = draft.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.isEmpty ? block : trimmed + "\n\n" + block
    }
}
