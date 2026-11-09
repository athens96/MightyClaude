import Foundation
import Testing
@testable import MightyCore

/// The change review's line notes (`ReviewNotes.swift`) as the follow-up request they become.
struct ReviewNotesTests {
    @Test func notesAreSortedByPlaceAndNameTheLineTheAgentCanOpen() {
        let notes = [
            ReviewNote(path: "b.swift", line: 3, side: .new, quote: "let x = 1", text: "rename x"),
            ReviewNote(path: "a.swift", line: 9, side: .old, quote: "  guard ok else { return }  ", text: "keep this guard"),
            ReviewNote(path: "a.swift", line: 2, side: .new, quote: "", text: "  why?\nexplain  "),
            ReviewNote(path: "c.swift", line: 1, side: .new, quote: "x", text: "   "),
        ]
        let prompt = ReviewNotes.prompt(notes, header: "Review notes:", removedLabel: "removed line {line}")
        #expect(prompt == """
        Review notes:
        - `a.swift:2`
          why?
          explain
        - `a.swift` (removed line 9) — `guard ok else { return }`
          keep this guard
        - `b.swift:3` — `let x = 1`
          rename x
        """)
    }

    @Test func emptyNotesMakeNoPromptAndTheBlockFollowsTheDraft() {
        #expect(ReviewNotes.prompt([], header: "h", removedLabel: "{line}").isEmpty)
        #expect(ReviewNotes.appending("notes", to: "  ") == "notes")
        #expect(ReviewNotes.appending("notes", to: "fix it\n") == "fix it\n\nnotes")
        #expect(ReviewNotes.appending("", to: "draft") == "draft")
    }

    @Test func aLongQuoteIsCutAndBackticksCannotBreakTheCodeSpan() {
        let long = String(repeating: "a", count: 200)
        let prompt = ReviewNotes.prompt([ReviewNote(path: "f", line: 1, side: .new, quote: long + "`", text: "n")], header: "h", removedLabel: "{line}")
        #expect(prompt.contains(String(repeating: "a", count: ReviewNotes.maximumQuote) + "…`"))
        let ticks = ReviewNotes.prompt([ReviewNote(path: "f", line: 1, side: .new, quote: "a `b` c", text: "n")], header: "h", removedLabel: "{line}")
        #expect(ticks.contains("`a 'b' c`"))
    }
}
