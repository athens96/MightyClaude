import Foundation
import Testing
@testable import MightyCore

/// One `insertText(_:replacementRange:)` as the input method delivers it.
/// `replaces` is nil when the range is NSNotFound: the editor reads "the input
/// method gave a range" from the location alone, so a zero-length range counts
/// as one. Modelling it as a count as well keeps the document faithful.
private struct InsertEvent {
    var text: String
    var replaces: Int?
    var hasMarkedText = false
    var koreanSource = true

    init(_ text: String, replaces: Int? = nil, hasMarkedText: Bool = false, koreanSource: Bool = true) {
        self.text = text
        self.replaces = replaces
        self.hasMarkedText = hasMarkedText
        self.koreanSource = koreanSource
    }
}

/// The editor: it asks the fallback what to do and applies the answer.
/// `sessionSuspect` is what the app computes from `NSApp.isActive` and the
/// current text input context; both recorded failures had it true.
private struct Editor {
    var fallback = HangulFallback()
    var sessionSuspect = true
    var text = ""
    var applied = 0

    mutating func handle(_ event: InsertEvent) -> HangulFallback.Decision {
        let decision = fallback.insert(
            event.text,
            hasReplacementRange: event.replaces != nil,
            hasMarkedText: event.hasMarkedText,
            koreanSource: event.koreanSource,
            sessionSuspect: sessionSuspect
        )
        switch decision {
        case .passThrough:
            HangulKeys.apply(HangulComposer.Edit(deleteBackward: event.replaces ?? 0, insert: event.text), to: &text)
        case let .apply(edit):
            HangulKeys.apply(edit, to: &text)
            applied += 1
        }
        return decision
    }

    mutating func deleteBackward() {
        if let edit = fallback.backspace() {
            HangulKeys.apply(edit, to: &text)
        } else if !text.isEmpty {
            text.removeLast()
        }
    }
}

/// Live 2-set streams in the shape `docs/input-method-diagnostics.md` describes
/// and the recorded traces show: the first jamo of a syllable arrives raw, every
/// continuation arrives as the whole syllable with a replacement range, and a
/// space or punctuation re-sends the syllable with a range before itself.
/// Written out by hand on purpose — deriving them from `HangulComposer` would
/// only prove the composer agrees with itself.
private let healthyTraces: [(sentence: String, events: [InsertEvent])] = [
    ("값이 없어?", [
        InsertEvent("ㄱ"), InsertEvent("가", replaces: 1), InsertEvent("갑", replaces: 1), InsertEvent("값", replaces: 1),
        // A consonant after a complete syllable, first variant: the raw jamo alone.
        InsertEvent("ㅇ"), InsertEvent("이", replaces: 1),
        InsertEvent("이", replaces: 1), InsertEvent(" "),
        InsertEvent("ㅇ"), InsertEvent("어", replaces: 1), InsertEvent("업", replaces: 1), InsertEvent("없", replaces: 1),
        // Second variant: the syllable is committed with a range, then the raw jamo.
        InsertEvent("없", replaces: 1), InsertEvent("ㅇ"),
        InsertEvent("어", replaces: 1),
        InsertEvent("어", replaces: 1), InsertEvent("?"),
    ]),
    ("과일 말고 감자", [
        InsertEvent("ㄱ"), InsertEvent("고", replaces: 1), InsertEvent("과", replaces: 1),
        InsertEvent("ㅇ"), InsertEvent("이", replaces: 1), InsertEvent("일", replaces: 1),
        InsertEvent("일", replaces: 1), InsertEvent(" "),
        InsertEvent("ㅁ"), InsertEvent("마", replaces: 1), InsertEvent("말", replaces: 1),
        // 말 + ㄱ is 맑, and ㅗ then moves the final into the next syllable: two
        // syllables replace one, which the editor must not read as a raw jamo.
        InsertEvent("맑", replaces: 1), InsertEvent("말고", replaces: 1),
        InsertEvent("고", replaces: 1), InsertEvent(" "),
        InsertEvent("ㄱ"), InsertEvent("가", replaces: 1), InsertEvent("감", replaces: 1),
        InsertEvent("감", replaces: 1), InsertEvent("ㅈ"), InsertEvent("자", replaces: 1),
    ]),
]

@Suite struct HangulFallbackTests {
    /// The load-bearing claim: a live input method never has a syllable changed
    /// under it. It has to hold whether or not the session looks suspect.
    @Test func recordedShapeHealthyStreamsAreNeverTouched() {
        for suspect in [false, true] {
            for trace in healthyTraces {
                var editor = Editor()
                editor.sessionSuspect = suspect
                for event in trace.events {
                    #expect(editor.handle(event) == .passThrough, "intervened in \(trace.sentence) at \(event.text)")
                }
                #expect(editor.text == trace.sentence && editor.applied == 0 && !editor.fallback.hasEngaged)
            }
        }
    }

    @Test func theRecordedFailureComposesAgain() {
        // ime-2026-09-19T155414Z.json: four raw jamo, no replacement range
        // anywhere, and the app inactive with another input context current.
        var editor = Editor()
        let decisions = ["ㅎ", "ㅏ", "ㄷ", "ㅏ"].map { editor.handle(InsertEvent($0)) }
        #expect(decisions[0] == .passThrough)                                                     // nothing to change yet
        #expect(decisions[1] == .apply(HangulComposer.Edit(deleteBackward: 1, insert: "하", previous: "ㅎ")))
        #expect(decisions[2] == .apply(HangulComposer.Edit(deleteBackward: 1, insert: "핟", previous: "하")))
        #expect(decisions[3] == .apply(HangulComposer.Edit(deleteBackward: 1, insert: "하다", previous: "핟")))
        #expect(editor.text == "하다" && editor.fallback.hasEngaged)
    }

    /// The input method can end a composition the editor never hears about
    /// (⌘Tab, 한자, an input source change). `ComposerTextView` resets on those,
    /// and this pins what that reset buys.
    @Test func aResetBetweenTwoRawJamoKeepsThemApart() {
        var editor = Editor()
        #expect(editor.handle(InsertEvent("ㄱ")) == .passThrough)
        editor.fallback.reset()                     // what the app's notification hooks do
        #expect(editor.handle(InsertEvent("ㅏ")) == .passThrough)
        #expect(editor.text == "ㄱㅏ" && !editor.fallback.hasEngaged)

        // Without the reset the same two events merge — right for a dead session,
        // wrong for one that only committed behind the app's back.
        var ungated = Editor()
        _ = ungated.handle(InsertEvent("ㄱ"))
        #expect(ungated.handle(InsertEvent("ㅏ")) == .apply(HangulComposer.Edit(deleteBackward: 1, insert: "가", previous: "ㄱ")))
        #expect(ungated.text == "가")
    }

    @Test func aHealthyLookingSessionNeverComposesHere() {
        var editor = Editor()
        editor.sessionSuspect = false
        for jamo in ["ㅎ", "ㅏ", "ㄷ", "ㅏ"] { #expect(editor.handle(InsertEvent(jamo)) == .passThrough) }
        #expect(editor.text == "ㅎㅏㄷㅏ" && editor.applied == 0 && !editor.fallback.hasEngaged)
        // Nothing is held either, so opening the gate later starts from scratch.
        #expect(!editor.fallback.isComposing)
    }

    @Test func theDetectorsProofOpensTheGateAndAReplacementClosesIt() {
        var editor = Editor()
        editor.sessionSuspect = false
        _ = editor.handle(InsertEvent("ㅎ"))
        editor.fallback.confirmBroken()             // the monitor saw two uncombined pairs
        _ = editor.handle(InsertEvent("ㅎ"))
        #expect(editor.handle(InsertEvent("ㅏ")) == .apply(HangulComposer.Edit(deleteBackward: 1, insert: "하", previous: "ㅎ")))
        #expect(editor.text == "ㅎ하")

        // A replacement range proves the input method is composing again.
        _ = editor.handle(InsertEvent("하", replaces: 1))
        _ = editor.handle(InsertEvent("ㄷ"))
        #expect(editor.handle(InsertEvent("ㅏ")) == .passThrough)
        #expect(editor.text == "ㅎ하ㄷㅏ")
    }

    @Test func brokenStreamsComposeOnlyWhenTheGateIsOpen() {
        for sentence in HangulKeys.sentences {
            let raw = HangulKeys.strokes(sentence).map { InsertEvent(String($0)) }

            var suspected = Editor()
            for event in raw { _ = suspected.handle(event) }
            #expect(suspected.text == sentence, "got \(suspected.text) for \(sentence)")
            #expect(suspected.fallback.hasEngaged)

            // The same stream with nothing suspect about the session stays raw.
            var healthy = Editor()
            healthy.sessionSuspect = false
            for event in raw { #expect(healthy.handle(event) == .passThrough) }
            #expect(healthy.text == String(HangulKeys.strokes(sentence)) && !healthy.fallback.hasEngaged)
        }
    }

    @Test func markedTextAndReplacementRangesEndTheComposition() {
        var marked = Editor()
        _ = marked.handle(InsertEvent("ㅎ"))
        _ = marked.handle(InsertEvent("ㅎ", hasMarkedText: true))
        #expect(marked.handle(InsertEvent("ㅏ")) == .passThrough)
        #expect(marked.text == "ㅎㅎㅏ" && !marked.fallback.hasEngaged)

        var replaced = Editor()
        _ = replaced.handle(InsertEvent("ㅎ"))
        _ = replaced.handle(InsertEvent("하", replaces: 1))
        #expect(replaced.handle(InsertEvent("ㅏ")) == .passThrough)
        #expect(replaced.text == "하ㅏ" && !replaced.fallback.hasEngaged)

        // A zero-length range is still a range: the app reads the location only.
        var empty = Editor()
        _ = empty.handle(InsertEvent("ㅎ"))
        _ = empty.handle(InsertEvent("ㅎ", replaces: 0))
        #expect(empty.handle(InsertEvent("ㅏ")) == .passThrough)
        #expect(empty.text == "ㅎㅎㅏ" && !empty.fallback.hasEngaged)
    }

    @Test func nonKoreanSourcesAndMultiCharacterInsertsEndTheComposition() {
        var latin = Editor()
        _ = latin.handle(InsertEvent("ㅎ"))
        _ = latin.handle(InsertEvent("a", koreanSource: false))
        #expect(latin.handle(InsertEvent("ㅏ")) == .passThrough)
        #expect(latin.text == "ㅎaㅏ")

        var pasted = Editor()
        _ = pasted.handle(InsertEvent("ㅎ"))
        _ = pasted.handle(InsertEvent("안녕하세요"))
        #expect(pasted.handle(InsertEvent("ㅏ")) == .passThrough)
        #expect(pasted.text == "ㅎ안녕하세요ㅏ")

        // A jamo under a non-2-set source is a paste or another layout, not typing.
        var foreignJamo = Editor()
        _ = foreignJamo.handle(InsertEvent("ㅎ"))
        #expect(foreignJamo.handle(InsertEvent("ㅏ", koreanSource: false)) == .passThrough)
        #expect(foreignJamo.text == "ㅎㅏ" && !foreignJamo.fallback.hasEngaged)
    }

    @Test func backspaceUnwindsWhatTheFallbackComposed() {
        var editor = Editor()
        for jamo in ["ㄱ", "ㅏ", "ㅂ", "ㅅ"] { _ = editor.handle(InsertEvent(jamo)) }
        #expect(editor.text == "값")
        for expected in ["갑", "가", "ㄱ", ""] {
            editor.deleteBackward()
            #expect(editor.text == expected)
        }
        // Nothing composing any more, so the editor's own delete runs.
        editor.text = "완료"
        editor.deleteBackward()
        #expect(editor.text == "완")
    }

    @Test func onlyTheTwoSetSourcesOpenTheFallback() {
        #expect(InputMethodSymptom.isTwoSetKoreanInputSource("com.apple.inputmethod.Korean.2SetKorean"))
        #expect(InputMethodSymptom.isTwoSetKoreanInputSource("com.apple.inputmethod.Korean"))
        #expect(!InputMethodSymptom.isTwoSetKoreanInputSource("com.apple.inputmethod.Korean.3SetKorean"))
        #expect(!InputMethodSymptom.isTwoSetKoreanInputSource("org.youknowone.inputmethod.Gureum.han3"))
        #expect(!InputMethodSymptom.isTwoSetKoreanInputSource(nil))
        // The symptom detector keeps its wider meaning: it only records.
        #expect(InputMethodSymptom.isKoreanInputSource("org.youknowone.inputmethod.Gureum.han3"))
    }
}
