import Foundation

/// Decides, for every `insertText` the editor receives, whether the input method
/// is still composing for us or whether we have to compose ourselves.
///
/// A live 2-set input method always carries a replacement range once a syllable
/// grows (ㅎ → 하 replace={n,1}), so rule 3 below never changes a character in a
/// healthy event stream. Raw jamo arriving one after another only happen in a
/// dead input session, and that is exactly where the composer takes over.
public struct HangulFallback: Sendable {
    public enum Decision: Equatable { case passThrough, apply(HangulComposer.Edit) }

    private var composer = HangulComposer()
    /// Latched once the symptom detector has proof, so a dead session we cannot
    /// recognise from app state still gets composed here.
    private var confirmedBroken = false
    /// Whether composing here ever changed what the input method delivered (diagnostics, banner).
    public private(set) var hasEngaged = false

    public init() {}

    /// Whether a syllable is being composed here rather than by the input method.
    public var isComposing: Bool { composer.isComposing }

    /// `text`: the inserted string, `hasReplacementRange`: the input method gave a range to replace,
    /// `hasMarkedText`: the editor holds marked text, `koreanSource`: the current input source is 2-set Korean,
    /// `sessionSuspect`: the app was inactive or the text input system pointed elsewhere for this key.
    public mutating func insert(
        _ text: String,
        hasReplacementRange: Bool,
        hasMarkedText: Bool,
        koreanSource: Bool,
        sessionSuspect: Bool
    ) -> Decision {
        // 1. The input method is alive and driving the composition.
        guard !hasReplacementRange, !hasMarkedText else {
            composer.reset()
            confirmedBroken = false
            return .passThrough
        }
        // 2. Anything that is not one Korean jamo ends the composition.
        guard koreanSource, text.count == 1, let jamo = text.first, HangulComposer.isCompatibilityJamo(jamo) else {
            composer.reset()
            return .passThrough
        }
        // 3. A lone jamo with no replacement range. Both recorded failures had the
        // app inactive and the input system on another context at that key; without
        // one of those signals a live input method may simply have committed behind
        // the app's back (⌘Tab, 한자), and merging its next jamo would be wrong.
        guard sessionSuspect || confirmedBroken else {
            composer.reset()
            return .passThrough
        }
        let edit = composer.input(jamo)
        // Inserting the jamo unchanged is what the editor would do anyway — only the state moved.
        if edit.deleteBackward == 0, edit.insert == text { return .passThrough }
        hasEngaged = true
        return .apply(edit)
    }

    public mutating func backspace() -> HangulComposer.Edit? { composer.backspace() }

    public mutating func reset() { composer.reset() }

    /// The symptom detector saw two uncombined jamo pairs: compose here whatever
    /// the session looks like, until the input method replaces a syllable again.
    public mutating func confirmBroken() { confirmedBroken = true }
}
