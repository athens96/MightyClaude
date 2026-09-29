import Foundation

/// Korean input methods take the click that ends a composition for
/// themselves: the syllable is committed, but the caret stays where the
/// composition was instead of moving to the click.
public enum ComposerCaret {
    /// Where the caret belongs once that commit has landed, or nil when the
    /// click was honoured anyway or the user has since selected something.
    /// `clicked` is the insertion index under the click, read while the
    /// composition was still in the text.
    public static func afterCompositionClick(clicked: Int, length: Int, selection: NSRange) -> Int? {
        guard selection.length == 0, selection.location != NSNotFound else { return nil }
        let target = max(0, min(clicked, length))
        return selection.location == target ? nil : target
    }
}
