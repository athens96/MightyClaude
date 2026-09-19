import Foundation

/// Apple's 2-set (두벌식) Hangul automaton, so the editor can keep composing
/// syllables when the OS input-method session dies and raw jamo arrive instead
/// of replaced syllables (`docs/hangul-fallback-composer.md`).
///
/// Output is always a precomposed syllable (U+AC00…U+D7A3) or a compatibility
/// jamo (U+3131…U+3163); conjoining jamo (the U+1100 block) never leave this type.
public struct HangulComposer: Sendable, Equatable {
    /// Delete `deleteBackward` UTF-16 units before the caret, then insert `insert`.
    /// `previous` is what those units must contain — the text the composer believes
    /// it put there, empty when nothing is deleted — so the editor can refuse an
    /// edit against text some other change moved under the caret.
    public struct Edit: Sendable, Equatable {
        public var deleteBackward: Int
        public var insert: String
        public var previous: String
        public init(deleteBackward: Int, insert: String, previous: String = "") {
            self.deleteBackward = deleteBackward
            self.insert = insert
            self.previous = previous
        }
    }

    /// 초성, 중성, 종성 of the syllable being composed, as compatibility jamo.
    private var lead: Character?
    private var vowel: Character?
    private var tail: Character?

    public init() {}

    public var isComposing: Bool { lead != nil || vowel != nil }

    /// A single compatibility jamo (U+3131…U+3163). Callers reset() for anything else.
    public mutating func input(_ jamo: Character) -> Edit {
        let composed = render()
        guard Self.isCompatibilityJamo(jamo) else {
            // Defensive: the contract says this never happens, but a stray character
            // must not be swallowed or glued onto the syllable.
            reset()
            return Edit(deleteBackward: 0, insert: String(jamo))
        }
        return Self.isVowel(jamo) ? insertVowel(jamo, over: composed) : insertConsonant(jamo, over: composed)
    }

    /// Undoes one jamo of the composing syllable, or nil when nothing is composing
    /// (then the editor's own delete applies, because finished text is not ours).
    public mutating func backspace() -> Edit? {
        let composed = render()
        guard !composed.isEmpty else { return nil }
        if let tail {
            self.tail = Self.finalClusters.first { $0.result == tail }?.base
        } else if let vowel {
            self.vowel = Self.vowelClusters.first { $0.result == vowel }?.base
        } else {
            lead = nil
        }
        return Edit(deleteBackward: composed.utf16.count, insert: render(), previous: composed)
    }

    /// Ends the composition (caret moved, non-jamo input, focus lost, draft replaced).
    public mutating func reset() {
        lead = nil
        vowel = nil
        tail = nil
    }

    public static func isCompatibilityJamo(_ c: Character) -> Bool {
        let scalars = c.unicodeScalars
        guard scalars.count == 1, let scalar = scalars.first else { return false }
        return (0x3131...0x3163).contains(scalar.value)
    }

    // MARK: - Automaton

    private mutating func insertConsonant(_ jamo: Character, over composed: String) -> Edit {
        if lead != nil, vowel != nil {
            if let tail {
                // 겹받침: only the eleven pairs merge, so a repeated key (ㄱㄱ) never doubles.
                if let merged = Self.finalClusters.first(where: { $0.base == tail && $0.added == jamo })?.result {
                    self.tail = merged
                    return Edit(deleteBackward: composed.utf16.count, insert: render(), previous: composed)
                }
            } else if Self.finals.contains(jamo) {
                // ㄸ ㅃ ㅉ are absent from `finals`, so they start a new syllable instead.
                tail = jamo
                return Edit(deleteBackward: composed.utf16.count, insert: render(), previous: composed)
            }
        }
        // What is composed stays in the document; this consonant opens the next syllable.
        reset()
        if Self.leads.contains(jamo) { lead = jamo }
        return Edit(deleteBackward: 0, insert: String(jamo))
    }

    private mutating func insertVowel(_ jamo: Character, over composed: String) -> Edit {
        if let tail {
            // 종성 이동: the final — its second consonant when it is a cluster — moves
            // into the next syllable (각+ㅏ → 가가, 닭+ㅏ → 달가, 값+ㅣ → 갑시).
            let cluster = Self.finalClusters.first { $0.result == tail }
            self.tail = cluster?.base
            let head = render()
            lead = cluster?.added ?? tail
            vowel = jamo
            self.tail = nil
            return Edit(deleteBackward: composed.utf16.count, insert: head + render(), previous: composed)
        }
        if let vowel {
            if let merged = Self.vowelClusters.first(where: { $0.base == vowel && $0.added == jamo })?.result {
                self.vowel = merged
                return Edit(deleteBackward: composed.utf16.count, insert: render(), previous: composed)
            }
            // Two vowels that do not combine (ㅏㅣ ≠ ㅐ): the first one is finished.
            reset()
            self.vowel = jamo
            return Edit(deleteBackward: 0, insert: render())
        }
        // With a lead this closes a syllable; without one the vowel stays a bare jamo.
        vowel = jamo
        return Edit(deleteBackward: composed.utf16.count, insert: render(), previous: composed)
    }

    /// The composing state as the user should see it right now.
    private func render() -> String {
        if let lead, let vowel,
           let leadIndex = Self.leads.firstIndex(of: lead),
           let vowelIndex = Self.vowels.firstIndex(of: vowel) {
            var tailIndex = 0
            if let tail, let index = Self.finals.firstIndex(of: tail) { tailIndex = index + 1 }
            let value = 0xAC00 + (leadIndex * 21 + vowelIndex) * 28 + tailIndex
            guard let scalar = UnicodeScalar(UInt32(value)) else { return "" }
            return String(Character(scalar))
        }
        if let lead { return String(lead) }
        if let vowel { return String(vowel) }
        return ""
    }

    private static func isVowel(_ jamo: Character) -> Bool {
        guard let scalar = jamo.unicodeScalars.first else { return false }
        return (0x314F...0x3163).contains(scalar.value)
    }

    // MARK: - 2-set tables

    private static let leads: [Character] = [
        "ㄱ", "ㄲ", "ㄴ", "ㄷ", "ㄸ", "ㄹ", "ㅁ", "ㅂ", "ㅃ", "ㅅ",
        "ㅆ", "ㅇ", "ㅈ", "ㅉ", "ㅊ", "ㅋ", "ㅌ", "ㅍ", "ㅎ",
    ]
    private static let vowels: [Character] = [
        "ㅏ", "ㅐ", "ㅑ", "ㅒ", "ㅓ", "ㅔ", "ㅕ", "ㅖ", "ㅗ", "ㅘ", "ㅙ",
        "ㅚ", "ㅛ", "ㅜ", "ㅝ", "ㅞ", "ㅟ", "ㅠ", "ㅡ", "ㅢ", "ㅣ",
    ]
    /// Jongseong in Unicode order; ㄸ ㅃ ㅉ are not finals.
    private static let finals: [Character] = [
        "ㄱ", "ㄲ", "ㄳ", "ㄴ", "ㄵ", "ㄶ", "ㄷ", "ㄹ", "ㄺ", "ㄻ", "ㄼ", "ㄽ", "ㄾ", "ㄿ",
        "ㅀ", "ㅁ", "ㅂ", "ㅄ", "ㅅ", "ㅆ", "ㅇ", "ㅈ", "ㅊ", "ㅋ", "ㅌ", "ㅍ", "ㅎ",
    ]
    /// The seven vowel pairs 2-set combines; every other pair stays two vowels.
    private static let vowelClusters: [(base: Character, added: Character, result: Character)] = [
        ("ㅗ", "ㅏ", "ㅘ"), ("ㅗ", "ㅐ", "ㅙ"), ("ㅗ", "ㅣ", "ㅚ"), ("ㅜ", "ㅓ", "ㅝ"),
        ("ㅜ", "ㅔ", "ㅞ"), ("ㅜ", "ㅣ", "ㅟ"), ("ㅡ", "ㅣ", "ㅢ"),
    ]
    /// The eleven final clusters. ㄲ and ㅆ are missing on purpose: they are one
    /// shifted key, never two presses of the same key.
    private static let finalClusters: [(base: Character, added: Character, result: Character)] = [
        ("ㄱ", "ㅅ", "ㄳ"), ("ㄴ", "ㅈ", "ㄵ"), ("ㄴ", "ㅎ", "ㄶ"), ("ㄹ", "ㄱ", "ㄺ"),
        ("ㄹ", "ㅁ", "ㄻ"), ("ㄹ", "ㅂ", "ㄼ"), ("ㄹ", "ㅅ", "ㄽ"), ("ㄹ", "ㅌ", "ㄾ"),
        ("ㄹ", "ㅍ", "ㄿ"), ("ㄹ", "ㅎ", "ㅀ"), ("ㅂ", "ㅅ", "ㅄ"),
    ]
}
