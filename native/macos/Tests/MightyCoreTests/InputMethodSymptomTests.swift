import Foundation
import Testing
@testable import MightyCore

struct InputMethodSymptomTests {
    @Test func loneJamoAndKoreanSourcesAreRecognised() {
        #expect(InputMethodSymptom.isLoneJamo("ㄱ") && InputMethodSymptom.isLoneJamo("ㅏ") && InputMethodSymptom.isLoneJamo("\u{1100}"))
        #expect(!InputMethodSymptom.isLoneJamo("가") && !InputMethodSymptom.isLoneJamo("a") && !InputMethodSymptom.isLoneJamo("ㄱㅏ") && !InputMethodSymptom.isLoneJamo(""))
        #expect(InputMethodSymptom.isKoreanInputSource("com.apple.inputmethod.Korean.2SetKorean"))
        #expect(InputMethodSymptom.isKoreanInputSource("org.youknowone.inputmethod.Gureum.han2"))
        #expect(!InputMethodSymptom.isKoreanInputSource("com.apple.keylayout.ABC") && !InputMethodSymptom.isKoreanInputSource(nil))
    }

    @Test func detectorFiresOnlyForRepeatedUncomposedJamoUnderAKoreanSource() {
        var detector = InputMethodSymptom.Detector(window: 5, threshold: 2)
        let r1 = detector.observeCommit("ㄱ", composedThisKey: false, koreanSource: true, at: 100)
        #expect(!r1)
        let r2 = detector.observeCommit("ㅏ", composedThisKey: false, koreanSource: true, at: 101)
        #expect(r2)
        detector.reset()
        // Normal composition commits syllables, or jamo after a marked-text update: never the symptom.
        let r3 = detector.observeCommit("가", composedThisKey: true, koreanSource: true, at: 200)
        #expect(!r3)
        let r4 = detector.observeCommit("ㄱ", composedThisKey: true, koreanSource: true, at: 201)
        #expect(!r4)
        let r5 = detector.observeCommit("ㄱ", composedThisKey: true, koreanSource: true, at: 202)
        #expect(!r5)
        // Jamo typed under an ASCII layout (e.g. pasted or a keyboard layout) is not the symptom.
        let r6 = detector.observeCommit("ㄱ", composedThisKey: false, koreanSource: false, at: 300)
        #expect(!r6)
        let r7 = detector.observeCommit("ㅏ", composedThisKey: false, koreanSource: false, at: 301)
        #expect(!r7)
        // Two events far apart do not accumulate.
        let r8 = detector.observeCommit("ㄱ", composedThisKey: false, koreanSource: true, at: 400)
        #expect(!r8)
        let r9 = detector.observeCommit("ㅏ", composedThisKey: false, koreanSource: true, at: 410)
        #expect(!r9)
        let r10 = detector.observeCommit("ㅗ", composedThisKey: false, koreanSource: true, at: 412)
        #expect(r10)
    }
}
