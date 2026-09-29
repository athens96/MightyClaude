import Foundation
import Testing
@testable import MightyCore

/// A click that ended a Korean composition left the caret at the end; once the
/// commit lands it goes where the user clicked, unless something else did.
@Suite struct ComposerCaretTests {
    @Test func aCaretLeftAtTheEndMovesToTheClick() {
        #expect(ComposerCaret.afterCompositionClick(clicked: 2, length: 6, selection: NSRange(location: 6, length: 0)) == 2)
    }

    @Test func aClickThatWasHonouredOrASelectionIsLeftAlone() {
        #expect(ComposerCaret.afterCompositionClick(clicked: 2, length: 6, selection: NSRange(location: 2, length: 0)) == nil)
        #expect(ComposerCaret.afterCompositionClick(clicked: 2, length: 6, selection: NSRange(location: 1, length: 3)) == nil)
        #expect(ComposerCaret.afterCompositionClick(clicked: 2, length: 6, selection: NSRange(location: NSNotFound, length: 0)) == nil)
    }

    @Test func aClickPastTheTextLandsAtItsEnd() {
        #expect(ComposerCaret.afterCompositionClick(clicked: 9, length: 6, selection: NSRange(location: 0, length: 0)) == 6)
        #expect(ComposerCaret.afterCompositionClick(clicked: 9, length: 6, selection: NSRange(location: 6, length: 0)) == nil)
    }
}
