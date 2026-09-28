import Foundation

/// §1.16.4: what one computed state widget draws, worked out in one place so
/// the Mac panel reads the payload exactly as the phone does
/// (`mobile/src/lib/styles.ts` — `parseWidgets` and `progressBarDisplay`).
/// Pure: the widgets come from the cached reading, never from disk.
public enum StyleWidgetPresentation: Sendable, Equatable {
    /// `fraction` is how full the track is (0…1); `text` is `done/total`, or
    /// the bare count when the bar has no total.
    case progressBar(fraction: Double, text: String)
    /// At most `maximumListItems` non-empty lines.
    case list(items: [String])
    /// One line; empty when the source has nothing to say.
    case label(text: String)

    /// The phone draws at most this many list lines, and so does the Mac.
    public static let maximumListItems = StyleStateEngine.maximumListItems
    /// The phone cuts a list line or a label at this many Unicode code points
    /// (`Array.from` there, `unicodeScalars` here), so both cut at one place.
    public static let maximumTextLength = 200

    /// Every widget in payload order. A bar with a negative count is dropped,
    /// as the phone drops it; everything else keeps its place, so an empty
    /// source still stands in the list (`0/0`, an empty list, an empty label).
    public static func make(_ widgets: [StylePanel.Widget]) -> [StyleWidgetPresentation] {
        widgets.compactMap(make)
    }

    public static func make(_ widget: StylePanel.Widget) -> StyleWidgetPresentation? {
        switch widget {
        case .progressBar(let value, let total):
            guard value >= 0 else { return nil }
            // A negative total is no total, as the phone reads it.
            guard let total, total >= 0 else { return .progressBar(fraction: 0, text: String(value)) }
            // More done than there are items never draws past a full bar.
            let done = min(value, total)
            let fraction = total > 0 ? Double(done) / Double(total) : 0
            return .progressBar(fraction: fraction, text: "\(done)/\(total)")
        case .list(let items):
            let lines = items.map(line).filter { !$0.isEmpty }
            return .list(items: Array(lines.prefix(maximumListItems)))
        case .label(let text):
            return .label(text: line(text))
        }
    }

    /// Whether there is anything to draw: an empty bar still draws its track
    /// and `0/0`; an empty list or label draws nothing.
    public var isEmpty: Bool {
        switch self {
        case .progressBar: return false
        case .list(let items): return items.isEmpty
        case .label(let text): return text.isEmpty
        }
    }

    /// One line as the phone's `inlineText` makes it: every stripped
    /// character removed wherever it stands (not only at the ends), then
    /// trimmed, then cut to `maximumTextLength` code points.
    static func line(_ text: String) -> String {
        var kept = String.UnicodeScalarView()
        kept.append(contentsOf: text.unicodeScalars.filter { !isStrippedFromLine($0) })
        let trimmed = String(kept).trimmingCharacters(in: .whitespacesAndNewlines)
        return String(String.UnicodeScalarView(trimmed.unicodeScalars.prefix(maximumTextLength)))
    }

    /// The phone's `UNSAFE_INLINE`: §1.11's banned set (`StyleText`) less
    /// U+200D (ZWJ), which joins a multi-scalar emoji into one glyph and so
    /// may stand on one line. Newline and tab are C0 controls: a line never
    /// breaks, it closes up.
    static func isStrippedFromLine(_ scalar: Unicode.Scalar) -> Bool {
        scalar.value != 0x200D && StyleText.isBanned(scalar)
    }
}
