import Foundation
import Testing
@testable import MightyCore

struct InputMethodSymptomTests {
    @Test func jamoClassesAndKoreanSourcesAreRecognised() {
        #expect(InputMethodSymptom.isLoneJamo("ㄱ") && InputMethodSymptom.isLoneJamo("ㅏ") && InputMethodSymptom.isLoneJamo("\u{1100}"))
        #expect(!InputMethodSymptom.isLoneJamo("가") && !InputMethodSymptom.isLoneJamo("a") && !InputMethodSymptom.isLoneJamo("ㄱㅏ") && !InputMethodSymptom.isLoneJamo(""))
        #expect(InputMethodSymptom.isKoreanInputSource("com.apple.inputmethod.Korean.2SetKorean"))
        #expect(InputMethodSymptom.isKoreanInputSource("org.youknowone.inputmethod.Gureum.han2"))
        #expect(!InputMethodSymptom.isKoreanInputSource("com.apple.keylayout.ABC") && !InputMethodSymptom.isKoreanInputSource(nil))
        // Only consonant + vowel left side by side is impossible for a working 2-set input.
        #expect(InputMethodSymptom.endsWithUncombinedSyllable("ㅇㅣ") && InputMethodSymptom.endsWithUncombinedSyllable("안녕 ㅎㅏ"))
        #expect(!InputMethodSymptom.endsWithUncombinedSyllable("ㅋㅋ"))      // consonants never combine
        #expect(!InputMethodSymptom.endsWithUncombinedSyllable("ㅏㅇ"))      // vowel then consonant is how a stray vowel looks
        #expect(!InputMethodSymptom.endsWithUncombinedSyllable("커ㅅ") && !InputMethodSymptom.endsWithUncombinedSyllable("ㅋ") && !InputMethodSymptom.endsWithUncombinedSyllable(""))
    }

    @Test func detectorIgnoresNormalReplacementTypingAndFiresOnUncombinedSyllables() {
        var detector = InputMethodSymptom.Detector(window: 10, threshold: 2)
        // Healthy 2-set typing: ㅋ → 커 → 컷 → 커서, each insert replacing the previous one.
        for (index, tail) in ["ㅋ", "커", "컷", "커서", "서 ", " ㅇ", "이", "잊", "이제"].enumerated() {
            let fired = detector.observeInsert(textBeforeCaret: tail, koreanSource: true, at: 100 + Double(index) * 0.1)
            #expect(!fired)
        }
        // Broken: the vowel lands next to the consonant instead of replacing it.
        let first = detector.observeInsert(textBeforeCaret: "ㅇㅣ", koreanSource: true, at: 200)
        let second = detector.observeInsert(textBeforeCaret: "ㅂㅓ", koreanSource: true, at: 201)
        #expect(!first && second)
        detector.reset()
        // Not under a Korean source, or too far apart: no alarm.
        let ascii1 = detector.observeInsert(textBeforeCaret: "ㅇㅣ", koreanSource: false, at: 300)
        let ascii2 = detector.observeInsert(textBeforeCaret: "ㅇㅣ", koreanSource: false, at: 301)
        #expect(!ascii1 && !ascii2)
        let far1 = detector.observeInsert(textBeforeCaret: "ㅇㅣ", koreanSource: true, at: 400)
        let far2 = detector.observeInsert(textBeforeCaret: "ㅇㅣ", koreanSource: true, at: 420)
        let near = detector.observeInsert(textBeforeCaret: "ㄹㅕ", koreanSource: true, at: 422)
        #expect(!far1 && !far2 && near)
    }
}
