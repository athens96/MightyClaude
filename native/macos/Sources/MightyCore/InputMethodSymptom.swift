import Foundation

/// Korean composition falling apart into jamo. Apple's 2-set Korean input
/// does not use marked text in NSTextView clients: it inserts the first jamo
/// and then *replaces* it through `insertText(_:replacementRange:)` as the
/// syllable grows (ㅋ → 커 → 컷). A lone jamo is therefore normal at the start
/// of every syllable. The broken state is the input method no longer
/// replacing: a consonant jamo is left standing directly before a vowel jamo
/// ("ㅇㅣ"), which a working 2-set input always combines ("이").
public enum InputMethodSymptom {
    /// U+3131…U+318E (Hangul compatibility jamo) or U+1100…U+11FF (conjoining jamo).
    public static func isLoneJamo(_ text: String) -> Bool {
        let scalars = text.unicodeScalars
        guard scalars.count == 1, let scalar = scalars.first else { return false }
        return (0x3131...0x318E).contains(scalar.value) || (0x1100...0x11FF).contains(scalar.value)
    }
    static func isConsonantJamo(_ scalar: Unicode.Scalar) -> Bool { (0x3131...0x314E).contains(scalar.value) }
    static func isVowelJamo(_ scalar: Unicode.Scalar) -> Bool { (0x314F...0x3163).contains(scalar.value) }

    /// True when the text right before the caret ends in consonant jamo + vowel jamo.
    public static func endsWithUncombinedSyllable(_ textBeforeCaret: String) -> Bool {
        let scalars = Array(textBeforeCaret.unicodeScalars.suffix(2))
        return scalars.count == 2 && isConsonantJamo(scalars[0]) && isVowelJamo(scalars[1])
    }

    public static func isKoreanInputSource(_ identifier: String?) -> Bool {
        guard let identifier else { return false }
        let lowered = identifier.lowercased()
        // Apple's Korean modes, Gureum (han2/han3 modes), and other Hangul IMEs.
        return ["korean", "hangul", "gureum", ".han2", ".han3", "2setkorean", "3setkorean"].contains { lowered.contains($0) }
    }

    /// Two uncombined syllables within `window` seconds confirm the symptom.
    public struct Detector: Sendable, Equatable {
        public var window: TimeInterval
        public var threshold: Int
        private var stamps: [TimeInterval] = []
        public init(window: TimeInterval = 10, threshold: Int = 2) { self.window = window; self.threshold = threshold }
        /// `textBeforeCaret`: the editor's text up to the caret after an insert.
        public mutating func observeInsert(textBeforeCaret: String, koreanSource: Bool, at time: TimeInterval) -> Bool {
            guard koreanSource, endsWithUncombinedSyllable(textBeforeCaret) else { return false }
            stamps = stamps.filter { time - $0 <= window } + [time]
            return stamps.count >= threshold
        }
        public mutating func reset() { stamps.removeAll() }
    }
}
