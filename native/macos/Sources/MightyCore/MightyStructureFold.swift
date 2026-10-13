import Foundation

extension MightyStructure {
    /// Finished requests fold in the 구조 view, so a long pane reads as a list: a request
    /// that completed, failed or stopped shows as one compact card (its heading, status,
    /// what was asked and how it ended on one line each) until the user opens it. A
    /// running or waiting request is always open, and one that finishes folds by itself
    /// unless the user opened it by hand. The diagram never folds.
    ///
    /// The open cards are the only state: a card is folded exactly while it is finished
    /// and the user has not opened it, so the fold on finishing needs no event of its own.
    /// The phone keeps the same rules (`mobile/src/lib/structure.ts`).
    public enum Fold {
        /// The card shows folded: finished, and not opened by hand.
        public static func isFolded(_ item: Item, unfolded: Set<String>) -> Bool {
            item.finished && !unfolded.contains(item.runID)
        }

        /// The cards of `items` that show folded.
        public static func folded(_ items: [Item], unfolded: Set<String>) -> Set<String> {
            Set(items.filter { isFolded($0, unfolded: unfolded) }.map(\.runID))
        }

        /// The hand-opened cards after the user opens or folds `item`'s card. Only a
        /// finished card has the control; an unfinished one is left as it is.
        public static func toggled(_ item: Item, unfolded: Set<String>) -> Set<String> {
            guard item.finished else { return unfolded }
            var next = unfolded
            if next.remove(item.runID) == nil { next.insert(item.runID) }
            return next
        }

        /// The folded card's result line: the answer's first line, without its Markdown
        /// marks. Nil for a request that failed or stopped, or completed without an
        /// answer: the card says how it ended instead.
        public static func resultLine(_ item: Item) -> String? {
            guard item.status == "completed", let text = item.answer?.text ?? item.result else { return nil }
            return firstLine(text)
        }

        /// The first line of `text` with words in it, without the Markdown that opens a
        /// line (a heading's #, a quote's >, a list's mark) or wraps a word (`, **, __).
        static func firstLine(_ text: String) -> String? {
            for raw in text.split(whereSeparator: \.isNewline) {
                var line = Substring(raw).drop { $0 == " " || $0 == "\t" }
                if line.hasPrefix("```") { continue }
                while let first = line.first, "#>".contains(first) { line = line.dropFirst().drop { $0 == " " } }
                if let first = line.first, "-*+".contains(first), line.dropFirst().first == " " { line = line.dropFirst(2) }
                if let dot = line.firstIndex(of: "."), dot != line.startIndex, line[..<dot].allSatisfy(\.isNumber),
                   line[line.index(after: dot)...].first == " " { line = line[line.index(dot, offsetBy: 2)...] }
                let cleaned = String(line).replacingOccurrences(of: "**", with: "").replacingOccurrences(of: "__", with: "")
                    .replacingOccurrences(of: "`", with: "").trimmingCharacters(in: .whitespaces)
                if !cleaned.isEmpty { return cleaned }
            }
            return nil
        }
    }
}
