import Foundation
import Testing
@testable import MightyCore

/// A block that gets shorter keeps its last lines in view for a reader who was
/// following them, and leaves a reader who scrolled up where they were.
@Suite struct TranscriptViewportTests {
    @Test func aReaderAtTheBottomStaysAtTheBottomWhenTheViewportShrinks() {
        // 1000pt document, 300pt viewport at the bottom, then 50pt shorter.
        #expect(TranscriptViewport.originAfterResize(previousOriginY: 700, previousVisibleHeight: 300, previousDocumentHeight: 1000,
                                                     visibleHeight: 250, documentHeight: 1000) == 750)
    }

    @Test func aReaderNearTheBottomCountsAsFollowing() {
        #expect(TranscriptViewport.isFollowing(originY: 670, visibleHeight: 300, documentHeight: 1000))
        #expect(TranscriptViewport.originAfterResize(previousOriginY: 670, previousVisibleHeight: 300, previousDocumentHeight: 1000,
                                                     visibleHeight: 250, documentHeight: 1000) == 750)
    }

    @Test func aReaderWhoScrolledUpIsLeftAlone() {
        #expect(!TranscriptViewport.isFollowing(originY: 300, visibleHeight: 300, documentHeight: 1000))
        #expect(TranscriptViewport.originAfterResize(previousOriginY: 300, previousVisibleHeight: 300, previousDocumentHeight: 1000,
                                                     visibleHeight: 250, documentHeight: 1000) == nil)
    }

    @Test func aReflowedDocumentIsFollowedToItsNewBottom() {
        // A narrower block wraps into a taller document.
        #expect(TranscriptViewport.originAfterResize(previousOriginY: 700, previousVisibleHeight: 300, previousDocumentHeight: 1000,
                                                     visibleHeight: 300, documentHeight: 1400) == 1100)
    }

    @Test func aShortDocumentNeverScrollsAboveItsTop() {
        #expect(TranscriptViewport.bottomOriginY(visibleHeight: 300, documentHeight: 120) == 0)
        #expect(TranscriptViewport.originAfterResize(previousOriginY: 0, previousVisibleHeight: 300, previousDocumentHeight: 300,
                                                     visibleHeight: 250, documentHeight: 250) == 0)
    }

    @Test func noViewportYetIsNotAPositionToKeep() {
        #expect(TranscriptViewport.originAfterResize(previousOriginY: 0, previousVisibleHeight: 0, previousDocumentHeight: 20,
                                                     visibleHeight: 300, documentHeight: 1000) == nil)
    }
}
