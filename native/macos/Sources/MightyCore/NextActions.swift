import Foundation

/// One suggestion from an Ouroboros reply's `◆ <state> → next: <actions>` breadcrumb.
public struct NextAction: Equatable, Sendable {
    /// The whole option as written.
    public let label: String
    /// What the composer receives: the command inside the option, or the label.
    public let fill: String
    public init(label: String, fill: String) { self.label = label; self.fill = fill }

    /// The label as a button shows and reads it: backticks are markup, not text.
    public var displayLabel: String {
        let plain = String(String.UnicodeScalarView(label.unicodeScalars.filter { $0 != "`" }))
        return NextActions.trim(Array(plain.unicodeScalars)).isEmpty ? label : plain
    }
}

/// Reads the breadcrumb an Ouroboros reply ends with into at most four
/// suggestions the user can drop into the composer. The phone implements the
/// same contract in `mobile/src/lib/next-actions.ts`; both are held to
/// `native/contracts/fixtures/next-actions.json` (see `native/contracts/README.md`).
///
/// Everything works on Unicode scalars, the phone's code points: matching is
/// literal with no canonical equivalence, so a decomposed `또는` is not a separator.
public enum NextActions {
    public static let maximum = 4
    typealias Scalars = [Unicode.Scalar]

    /// The only characters trimmed or taken as a gap, the same set on the phone.
    private static let whitespace: Set<Unicode.Scalar> = [" ", "\t", "\r", "\n", "\u{3000}"]
    private static let diamond: Unicode.Scalar = "◆"
    private static let markers: [Scalars] = ["→ next:", "-> next:"].map(scalars)
    /// Tried in this order at each position, so `, or` wins over the ` or` inside it.
    /// An em dash splits only when `or`/`또는` follows it; otherwise it starts a note.
    private static let separators: [Scalars] = [", 또는 ", ", or ", " — 또는 ", " — or ", " 또는 ", " or "].map(scalars)
    /// A comma list is a choice only when it says so at its end.
    private static let choiceSuffixes: [Scalars] = [" 중에서 선택", " 중 선택", " 중 하나"].map(scalars)
    private static let alternatives: [Scalars] = ["또는", "or"].map(scalars)
    /// `(?![\s\S])` is the end of the text in both regex engines, unlike `$`.
    private static let command = try! NSRegularExpression(pattern: #"^(?:ooo(?:[ \t]|(?![\s\S]))|/[A-Za-z][A-Za-z0-9_-]*(?::[A-Za-z0-9_-]+)?(?:[ \t]|(?![\s\S])))"#)

    /// The text after `next:` on the last breadcrumb line, if there is one.
    public static func breadcrumbNext(_ text: String) -> String? {
        guard text.unicodeScalars.contains(diamond) else { return nil }
        let all = scalars(text)
        for raw in all.split(separator: "\n", omittingEmptySubsequences: false).reversed() {
            let line = trim(Array(raw))
            guard line.first == diamond else { continue }
            let found = markers.compactMap { marker in find(marker, in: line).map { (at: $0, end: $0 + marker.count) } }.min { $0.at < $1.at }
            if let found { return string(trim(Array(line[found.end...]))) }
        }
        return nil
    }

    /// The suggestions in a reply's breadcrumb; empty when it has none.
    public static func parse(_ text: String) -> [NextAction] {
        guard let next = breadcrumbNext(text), !next.isEmpty else { return [] }
        var list = scalars(next)
        let suffix = choiceSuffixes.first { list.count >= $0.count && list.suffix($0.count).elementsEqual($0) }
        if let suffix { list.removeLast(suffix.count) }
        var options = splitTopLevel(list, separators: separators)
        if suffix != nil { options = options.flatMap { splitTopLevel($0, separators: [[","]]) } }
        let actions = options
            .map { trim(stripLeadingAlternative(trim($0))) }
            .filter { !$0.isEmpty }
            .map { NextAction(label: string($0), fill: fill(for: $0)) }
            .filter { !trim(scalars($0.fill)).isEmpty }
        return Array(actions.prefix(maximum))
    }

    /// The reply whose suggestions are on offer: the last assistant entry with
    /// no user entry after it. The caller still hides them while the pane runs.
    public static func latest(in entries: [LogEntry]) -> (entryId: String, actions: [NextAction])? {
        for entry in entries.reversed() {
            if entry.kind == "user" { return nil }
            guard entry.kind == "assistant" else { continue }
            let actions = parse(entry.text)
            return actions.isEmpty ? nil : (entry.id, actions)
        }
        return nil
    }

    /// How a fill enters the composer without losing the draft: a blank draft
    /// is replaced by the fill, anything else keeps its text and gets the fill
    /// on a new line after it (no extra line when it already ends with one).
    public static func insertion(into draft: String, fill: String) -> (replacesDraft: Bool, text: String) {
        if trim(scalars(draft)).isEmpty { return (true, fill) }
        return (false, draft.unicodeScalars.last == "\n" ? fill : "\n" + fill)
    }

    static func scalars(_ text: String) -> Scalars { Array(text.unicodeScalars) }
    private static func string(_ scalars: Scalars) -> String { String(String.UnicodeScalarView(scalars)) }

    static func trim(_ text: Scalars) -> Scalars {
        var start = 0, end = text.count
        while start < end, whitespace.contains(text[start]) { start += 1 }
        while end > start, whitespace.contains(text[end - 1]) { end -= 1 }
        return Array(text[start..<end])
    }

    /// Only ASCII letters fold, so both platforms compare the same characters.
    private static func asciiLower(_ scalar: Unicode.Scalar) -> Unicode.Scalar {
        guard scalar.value >= 65, scalar.value <= 90, let lower = Unicode.Scalar(scalar.value + 32) else { return scalar }
        return lower
    }

    private static func matches(_ text: Scalars, at index: Int, _ wanted: Scalars) -> Bool {
        guard index + wanted.count <= text.count else { return false }
        return wanted.indices.allSatisfy { asciiLower(text[index + $0]) == wanted[$0] }
    }

    /// The first literal occurrence, as `indexOf` finds it on the phone.
    private static func find(_ needle: Scalars, in text: Scalars) -> Int? {
        guard needle.count <= text.count else { return nil }
        return (0...(text.count - needle.count)).first { text[$0..<($0 + needle.count)].elementsEqual(needle) }
    }

    /// Splits outside backtick code spans and parentheses.
    private static func splitTopLevel(_ text: Scalars, separators: [Scalars]) -> [Scalars] {
        var parts: [Scalars] = []
        var current: Scalars = []
        var inCode = false
        var depth = 0
        var index = 0
        while index < text.count {
            let scalar = text[index]
            if !inCode, depth == 0, let separator = separators.first(where: { matches(text, at: index, $0) }) {
                parts.append(current)
                current = []
                index += separator.count
                continue
            }
            if scalar == "`" { inCode.toggle() }
            else if !inCode, scalar == "(" { depth += 1 }
            else if !inCode, scalar == ")" { depth = max(0, depth - 1) }
            current.append(scalar)
            index += 1
        }
        parts.append(current)
        return parts
    }

    /// Drops a leading `또는`/`or` (ASCII case-insensitive) that a gap follows.
    private static func stripLeadingAlternative(_ text: Scalars) -> Scalars {
        for word in alternatives where matches(text, at: 0, word) && text.count > word.count && whitespace.contains(text[word.count]) {
            return Array(text[word.count...])
        }
        return text
    }

    /// Backtick pairs in order; an empty pair or an unclosed backtick yields nothing.
    private static func codeSpans(_ text: Scalars) -> [Scalars] {
        var spans: [Scalars] = []
        var open: Scalars?
        for scalar in text {
            if scalar != "`" { open?.append(scalar) }
            else if let span = open {
                if !span.isEmpty { spans.append(span) }
                open = nil
            } else { open = [] }
        }
        return spans
    }

    private static func isCommand(_ text: Scalars) -> Bool {
        let value = string(text)
        return command.firstMatch(in: value, range: NSRange(value.startIndex..., in: value)) != nil
    }

    /// The first command-like code span, or a span that is the whole option.
    private static func fill(for label: Scalars) -> String {
        let spans = codeSpans(label)
        if let found = spans.first(where: { isCommand(trim($0)) }) { return string(trim(found)) }
        if spans.count == 1, trim(label) == ["`"] + spans[0] + ["`"] { return string(trim(spans[0])) }
        return string(label)
    }
}
