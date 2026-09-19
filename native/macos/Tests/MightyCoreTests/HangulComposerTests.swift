import Foundation
import Testing
@testable import MightyCore

/// Turns Korean text into the 2-set key strokes it is typed with, and back.
/// Shared with `HangulFallbackTests`.
enum HangulKeys {
    /// Every syllable becomes its jamo keys; compound vowels and final clusters
    /// become the two keys they are pressed with. Anything else passes through.
    static func strokes(_ text: String) -> [Character] {
        var keys: [Character] = []
        for character in text {
            let scalars = character.unicodeScalars
            guard scalars.count == 1, let scalar = scalars.first, (0xAC00...0xD7A3).contains(scalar.value) else {
                keys.append(character)
                continue
            }
            let index = Int(scalar.value) - 0xAC00
            keys.append(leads[index / 588])
            let vowel = vowels[(index % 588) / 28]
            keys.append(contentsOf: vowelKeys[vowel] ?? [vowel])
            if index % 28 > 0 {
                let final = finals[index % 28 - 1]
                keys.append(contentsOf: finalKeys[final] ?? [final])
            }
        }
        return keys
    }

    /// Feeds the strokes to a fresh composer the way the editor would.
    static func typed(_ keys: [Character]) -> String {
        var composer = HangulComposer()
        var text = ""
        for key in keys {
            if HangulComposer.isCompatibilityJamo(key) {
                apply(composer.input(key), to: &text)
            } else {
                composer.reset()
                text.append(key)
            }
        }
        return text
    }

    /// The editor side of an `Edit`: drop UTF-16 units before the caret, then insert.
    static func apply(_ edit: HangulComposer.Edit, to text: inout String) {
        if edit.deleteBackward > 0 {
            let cut = text.utf16.index(text.utf16.endIndex, offsetBy: -edit.deleteBackward)
            text = String(text[..<cut])
        }
        text.append(edit.insert)
    }

    static let leads: [Character] = [
        "ㄱ", "ㄲ", "ㄴ", "ㄷ", "ㄸ", "ㄹ", "ㅁ", "ㅂ", "ㅃ", "ㅅ",
        "ㅆ", "ㅇ", "ㅈ", "ㅉ", "ㅊ", "ㅋ", "ㅌ", "ㅍ", "ㅎ",
    ]
    static let vowels: [Character] = [
        "ㅏ", "ㅐ", "ㅑ", "ㅒ", "ㅓ", "ㅔ", "ㅕ", "ㅖ", "ㅗ", "ㅘ", "ㅙ",
        "ㅚ", "ㅛ", "ㅜ", "ㅝ", "ㅞ", "ㅟ", "ㅠ", "ㅡ", "ㅢ", "ㅣ",
    ]
    static let finals: [Character] = [
        "ㄱ", "ㄲ", "ㄳ", "ㄴ", "ㄵ", "ㄶ", "ㄷ", "ㄹ", "ㄺ", "ㄻ", "ㄼ", "ㄽ", "ㄾ", "ㄿ",
        "ㅀ", "ㅁ", "ㅂ", "ㅄ", "ㅅ", "ㅆ", "ㅇ", "ㅈ", "ㅊ", "ㅋ", "ㅌ", "ㅍ", "ㅎ",
    ]
    static let vowelKeys: [Character: [Character]] = [
        "ㅘ": ["ㅗ", "ㅏ"], "ㅙ": ["ㅗ", "ㅐ"], "ㅚ": ["ㅗ", "ㅣ"], "ㅝ": ["ㅜ", "ㅓ"],
        "ㅞ": ["ㅜ", "ㅔ"], "ㅟ": ["ㅜ", "ㅣ"], "ㅢ": ["ㅡ", "ㅣ"],
    ]
    static let finalKeys: [Character: [Character]] = [
        "ㄳ": ["ㄱ", "ㅅ"], "ㄵ": ["ㄴ", "ㅈ"], "ㄶ": ["ㄴ", "ㅎ"], "ㄺ": ["ㄹ", "ㄱ"],
        "ㄻ": ["ㄹ", "ㅁ"], "ㄼ": ["ㄹ", "ㅂ"], "ㄽ": ["ㄹ", "ㅅ"], "ㄾ": ["ㄹ", "ㅌ"],
        "ㄿ": ["ㄹ", "ㅍ"], "ㅀ": ["ㄹ", "ㅎ"], "ㅄ": ["ㅂ", "ㅅ"],
    ]

    /// The 33 jamo a 2-set keyboard sends; the doubled consonants are one
    /// shifted press, so they appear among the leads and never as two keys.
    static let keyboard: [Character] = leads + [
        "ㅏ", "ㅐ", "ㅑ", "ㅒ", "ㅓ", "ㅔ", "ㅕ", "ㅖ", "ㅗ", "ㅛ", "ㅜ", "ㅠ", "ㅡ", "ㅣ",
    ]

    /// Every key sequence of 1…`depth` presses, for the ownership property test.
    static func sequences(upTo depth: Int) -> [[Character]] {
        var all: [[Character]] = []
        var level: [[Character]] = [[]]
        for _ in 0..<depth {
            level = level.flatMap { prefix in keyboard.map { prefix + [$0] } }
            all += level
        }
        return all
    }

    /// Sentences that between them cover compound vowels, every kind of final,
    /// final-to-initial migration and non-Korean characters.
    static let sentences = [
        "자모 안 되는 건 완전히 재설계가 필요해",
        "닭을 삶았다",
        "값이 없어",
        "꽃잎",
        "ㅋㅋㅋ 웃음",
        "의사와 왼쪽 귀",
        "뭐 했어?",
        "웬 인쇄물이 왔다",
    ]
}

@Suite struct HangulComposerTests {
    /// Runs the keys through one composer and reports what the document holds.
    private func type(_ keys: String) -> String { HangulKeys.typed(Array(keys)) }

    @Test func leadAndVowelMakeASyllable() {
        var composer = HangulComposer()
        #expect(!composer.isComposing)
        #expect(composer.input("ㄱ") == HangulComposer.Edit(deleteBackward: 0, insert: "ㄱ"))
        #expect(composer.isComposing)
        // The bare lead is one UTF-16 unit and gets replaced by the syllable.
        #expect(composer.input("ㅏ") == HangulComposer.Edit(deleteBackward: 1, insert: "가", previous: "ㄱ"))
        composer.reset()
        #expect(!composer.isComposing)
        #expect(type("ㅎㅏㄴㄱㅡㄹ") == "한글")
    }

    @Test func onlyTheSevenVowelPairsCombine() {
        #expect(type("ㄱㅗㅏ") == "과" && type("ㄱㅗㅐ") == "괘" && type("ㄱㅗㅣ") == "괴")
        #expect(type("ㄱㅜㅓ") == "궈" && type("ㄱㅜㅔ") == "궤" && type("ㄱㅜㅣ") == "귀" && type("ㄱㅡㅣ") == "긔")
        // ㅏㅣ is not ㅐ, with or without a lead.
        #expect(type("ㅏㅣ") == "ㅏㅣ" && type("ㄱㅏㅣ") == "가ㅣ")
        #expect(type("ㅗㅏ") == "ㅘ" && type("ㅗㅐ") == "ㅙ")
    }

    @Test func finalsAndClustersFollowTheKeyboard() {
        #expect(type("ㄱㅏㄱ") == "각" && type("ㄷㅏㄹㄱ") == "닭" && type("ㄱㅏㅂㅅ") == "값")
        #expect(type("ㅇㅓㅂㅅ") == "없" && type("ㅇㅏㄴㅎ") == "않" && type("ㅅㅏㄹㅁ") == "삶")
        // A repeated key never doubles: ㄱㄱ is not ㄲ, ㅅㅅ is not ㅆ.
        #expect(type("ㄱㅏㄱㄱ") == "각ㄱ" && type("ㄱㅏㅅㅅ") == "갓ㅅ")
        // ㄸ ㅃ ㅉ can never be finals, so they open the next syllable.
        #expect(type("ㄱㅏㄸ") == "가ㄸ" && type("ㄱㅏㅃ") == "가ㅃ" && type("ㄱㅏㅉ") == "가ㅉ")
        #expect(type("ㄱㅏㄸㅏ") == "가따")
        // A final that does not extend the cluster starts a new syllable.
        #expect(type("ㄱㅏㄴㄷ") == "간ㄷ" && type("ㄷㅏㄹㄱㅅ") == "닭ㅅ")
    }

    @Test func finalsMigrateIntoTheNextSyllable() {
        #expect(type("ㄱㅏㄱㅏ") == "가가")
        #expect(type("ㄷㅏㄹㄱㅏ") == "달가")
        #expect(type("ㄱㅏㅂㅅㅣ") == "갑시")
        #expect(type("ㅇㅓㅂㅅㅓ") == "업서" && type("ㅁㅏㄴㅎㅣ") == "만히")
    }

    @Test func vowelsWithoutALeadStayBare() {
        #expect(type("ㅏ") == "ㅏ" && type("ㅡㅣ") == "ㅢ" && type("ㅏㅏ") == "ㅏㅏ")
        // A lead on its own also shows as a bare jamo, and consonants never merge.
        #expect(type("ㄱ") == "ㄱ" && type("ㅋㅋㅋ") == "ㅋㅋㅋ")
        #expect(type("ㅏㄱㅏ") == "ㅏ가")
    }

    @Test func backspaceUnwindsTheComposingSyllable() {
        var composer = HangulComposer()
        var text = ""
        for key in "ㄱㅏㅂㅅ" { HangulKeys.apply(composer.input(key), to: &text) }
        #expect(text == "값")
        // 값 → 갑 → 가 → ㄱ → nothing, one jamo per press.
        for expected in ["갑", "가", "ㄱ", ""] {
            guard let edit = composer.backspace() else {
                Issue.record("backspace stopped before \(expected)")
                return
            }
            HangulKeys.apply(edit, to: &text)
            #expect(text == expected)
        }
        // Finished text is not ours: the editor's own delete takes over.
        #expect(composer.backspace() == nil && !composer.isComposing)
        // Compound vowels come apart one key at a time too.
        composer = HangulComposer()
        text = ""
        for key in "ㄱㅗㅏ" { HangulKeys.apply(composer.input(key), to: &text) }
        #expect(text == "과")
        for expected in ["고", "ㄱ", ""] {
            if let edit = composer.backspace() { HangulKeys.apply(edit, to: &text) }
            #expect(text == expected)
        }
    }

    @Test func sentencesSurviveTheRoundTripThroughKeyStrokes() {
        for sentence in HangulKeys.sentences {
            let typed = HangulKeys.typed(HangulKeys.strokes(sentence))
            #expect(typed == sentence, "typed \(typed) for \(sentence)")
        }
    }

    /// The eleven final clusters, as literals rather than as a reading of the
    /// implementation's tables: each formed by its two keys, moved into the next
    /// syllable by a vowel, and unwound one step by backspace.
    @Test func everyFinalClusterFormsMigratesAndUnwinds() {
        let clusters: [(keys: String, formed: String, migrated: String, unwound: String)] = [
            ("ㄱㅏㄱㅅ", "갃", "각사", "각"),
            ("ㄱㅏㄴㅈ", "갅", "간자", "간"),
            ("ㄱㅏㄴㅎ", "갆", "간하", "간"),
            ("ㄱㅏㄹㄱ", "갉", "갈가", "갈"),
            ("ㄱㅏㄹㅁ", "갊", "갈마", "갈"),
            ("ㄱㅏㄹㅂ", "갋", "갈바", "갈"),
            ("ㄱㅏㄹㅅ", "갌", "갈사", "갈"),
            ("ㄱㅏㄹㅌ", "갍", "갈타", "갈"),
            ("ㄱㅏㄹㅍ", "갎", "갈파", "갈"),
            ("ㄱㅏㄹㅎ", "갏", "갈하", "갈"),
            ("ㄱㅏㅂㅅ", "값", "갑사", "갑"),
        ]
        for cluster in clusters {
            #expect(type(cluster.keys) == cluster.formed, "typing \(cluster.keys)")
            #expect(type(cluster.keys + "ㅏ") == cluster.migrated, "typing \(cluster.keys)ㅏ")
            var composer = HangulComposer()
            var text = ""
            for key in cluster.keys { HangulKeys.apply(composer.input(key), to: &text) }
            if let edit = composer.backspace() { HangulKeys.apply(edit, to: &text) }
            #expect(text == cluster.unwound, "backspace of \(cluster.formed)")
        }
    }

    /// Vowels the sentences never reach, and the decided answer for a backspace
    /// after a final has migrated (`docs/hangul-fallback-composer.md` §3.1).
    @Test func rareVowelsAndBackspaceAfterAMigration() {
        #expect(type("ㄱㅑ") == "갸" && type("ㄱㅒ") == "걔" && type("ㄱㅠ") == "규")
        // Only the syllable after the migration is still being composed, so that
        // is all backspace unwinds; the one before it is finished text.
        let migrations = [("ㄱㅏㄱㅏ", "가가", "가ㄱ"), ("ㄷㅏㄹㄱㅏ", "달가", "달ㄱ"), ("ㄱㅏㅂㅅㅣ", "갑시", "갑ㅅ")]
        for (keys, migrated, afterBackspace) in migrations {
            var composer = HangulComposer()
            var text = ""
            for key in keys { HangulKeys.apply(composer.input(key), to: &text) }
            #expect(text == migrated, "typing \(keys)")
            if let edit = composer.backspace() { HangulKeys.apply(edit, to: &text) }
            #expect(text == afterBackspace, "backspace of \(migrated)")
        }
    }

    /// Everything the composer asks the editor to delete has to be text the
    /// composer itself put there, and the backspace chain has to give all of it
    /// back without reaching into what came before. Checked over every key
    /// sequence up to three presses long.
    @Test func editsOnlyEverTouchWhatTheComposerOwns() {
        let prefix = "메모 "
        for keys in HangulKeys.sequences(upTo: 3) {
            var composer = HangulComposer()
            var text = prefix
            for key in keys { apply(composer.input(key), to: &text, keeping: prefix) }
            var steps = 0
            while steps < keys.count + 2, let edit = composer.backspace() {
                apply(edit, to: &text, keeping: prefix)
                steps += 1
            }
            #expect(composer.backspace() == nil, "backspace did not terminate for \(String(keys))")
            #expect(text.hasPrefix(prefix), "ate into the text before the composition for \(String(keys))")
        }
    }

    /// One step of the ownership invariant: the edit names the text it deletes,
    /// that text is really there, and it is text the composition owns.
    private func apply(_ edit: HangulComposer.Edit, to text: inout String, keeping prefix: String) {
        #expect(edit.deleteBackward == edit.previous.utf16.count)
        #expect(edit.deleteBackward <= text.utf16.count - prefix.utf16.count)
        let cut = text.utf16.index(text.utf16.endIndex, offsetBy: -min(edit.deleteBackward, text.utf16.count))
        #expect(String(text[cut...]) == edit.previous)
        HangulKeys.apply(edit, to: &text)
    }

    @Test func outputIsOnlySyllablesAndCompatibilityJamo() {
        for sentence in HangulKeys.sentences {
            let keys = HangulKeys.strokes(sentence)
            // Every prefix of the key sequence, so half-composed states are covered too.
            for count in 0...keys.count {
                for scalar in HangulKeys.typed(Array(keys.prefix(count))).unicodeScalars where scalar.value > 0x7F {
                    let precomposed = (0xAC00...0xD7A3).contains(scalar.value)
                    let compatibility = (0x3131...0x3163).contains(scalar.value)
                    #expect(precomposed || compatibility, "unexpected scalar U+\(String(scalar.value, radix: 16)) from \(sentence)")
                }
            }
        }
    }
}
