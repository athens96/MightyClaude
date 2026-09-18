import Foundation
import Testing
@testable import MightyCore

/// The rule a Mighty graph card uses to take ⌘C back after the canvas monitor
/// moved the first responder away from it.
struct TranscriptCopyTests {
    private let command: UInt = 1 << 20
    private let shift: UInt = 1 << 17
    private let option: UInt = 1 << 19
    private let capsLock: UInt = 1 << 16
    private let function: UInt = 1 << 23
    private let numericPad: UInt = 1 << 21

    @Test func onlyCommandAloneOnTheCKeyCountsAsCopy() {
        #expect(TranscriptCopyClaim.isCopyKeyEquivalent(modifiers: command, characters: "c", keyCode: 8))
        // A Korean input source hands AppKit "ㅊ"; the key code is all that is
        // left to recognise the press by.
        #expect(TranscriptCopyClaim.isCopyKeyEquivalent(modifiers: command, characters: "ㅊ", keyCode: 8))
        #expect(TranscriptCopyClaim.isCopyKeyEquivalent(modifiers: command, characters: nil, keyCode: 8))
        // A layout that puts "c" elsewhere is still a copy.
        #expect(TranscriptCopyClaim.isCopyKeyEquivalent(modifiers: command, characters: "c", keyCode: 55))
        // Command and nothing else: ⇧⌘C and ⌥⌘C are other people's shortcuts.
        #expect(!TranscriptCopyClaim.isCopyKeyEquivalent(modifiers: command | shift, characters: "C", keyCode: 8))
        #expect(!TranscriptCopyClaim.isCopyKeyEquivalent(modifiers: command | option, characters: "ç", keyCode: 8))
        #expect(!TranscriptCopyClaim.isCopyKeyEquivalent(modifiers: 0, characters: "c", keyCode: 8))
        #expect(!TranscriptCopyClaim.isCopyKeyEquivalent(modifiers: command, characters: "v", keyCode: 9))
    }

    /// Caps Lock stays on for a whole Korean typing session, and every key on a
    /// laptop keyboard can arrive with the Fn or numeric-pad bit set. None of
    /// them change which shortcut the user meant.
    @Test func incidentalModifiersDoNotDefeatDetection() {
        #expect(TranscriptCopyClaim.isCopyKeyEquivalent(modifiers: command | capsLock, characters: "ㅊ", keyCode: 8))
        #expect(TranscriptCopyClaim.isCopyKeyEquivalent(modifiers: command | function, characters: "c", keyCode: 8))
        #expect(TranscriptCopyClaim.isCopyKeyEquivalent(modifiers: command | numericPad, characters: "c", keyCode: 8))
        #expect(TranscriptCopyClaim.isCopyKeyEquivalent(modifiers: command | capsLock | function | numericPad, characters: "ㅊ", keyCode: 8))
        // Cleared, not ignored: a real extra modifier still disqualifies.
        #expect(!TranscriptCopyClaim.isCopyKeyEquivalent(modifiers: command | capsLock | shift, characters: "c", keyCode: 8))
        #expect(!TranscriptCopyClaim.isCopyKeyEquivalent(modifiers: capsLock | function, characters: "c", keyCode: 8))
    }

    @Test func onlyAFocusThatCannotCopyForItselfGivesTheCardTheKey() {
        // The graph canvas probe is a plain NSView: it has no copy(_:) at all.
        let canvas = TranscriptCopyClaim.Responder.plain
        // Everything that answers Copy on its own — the composer with a
        // selection or with nothing but a caret, an NSTextField's field editor,
        // the Ghostty terminal view, another card.
        let canCopy = TranscriptCopyClaim.Responder(canCopyItself: true)
        func claims(_ responder: TranscriptCopyClaim.Responder, copy: Bool = true, selection: Int = 12,
                    recent: Bool = true, visible: Bool = true, onScreen: Bool = true, blocked: Bool = false) -> Bool {
            TranscriptCopyClaim.claims(copyKeyEquivalent: copy, selectionLength: selection,
                                       isMostRecentlySelected: recent, isVisible: visible,
                                       isSelectionOnScreen: onScreen, isBlocked: blocked, responder: responder)
        }
        // Focus on the graph canvas: the card is the only thing with a
        // selection, so it is the only thing ⌘C can mean.
        #expect(claims(canvas))
        // An allowlist, not a guess: a focused terminal, composer or field
        // editor keeps its own ⌘C whether or not it has a selection, which is
        // what makes an empty-selection ⌘C stay the no-op macOS users expect.
        #expect(!claims(canCopy))
        #expect(!claims(canCopy, selection: 40))
        // Nothing selected here, an older card's selection, an unmounted or
        // hidden card, a selection scrolled out of the card's clip, a sheet in
        // front of all of it, and any other key: none of this view's business.
        #expect(!claims(canvas, selection: 0))
        #expect(!claims(canvas, recent: false))
        #expect(!claims(canvas, visible: false))
        #expect(!claims(canvas, onScreen: false))
        #expect(!claims(canvas, blocked: true))
        #expect(!claims(canvas, copy: false))
    }
}
