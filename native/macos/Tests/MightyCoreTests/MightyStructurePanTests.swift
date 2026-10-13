import CoreGraphics
import Foundation
import Testing
@testable import MightyCore

/// Dragging the Mac 구조 view's empty space moves its page as the diagram's canvas moves.
struct MightyStructurePanTests {
    private let minimum = CGPoint(x: 0, y: 0)
    private let maximum = CGPoint(x: 240, y: 1_800)

    @Test func theGrabbedPointStaysUnderThePointer() {
        let start = CGPoint(x: 100, y: 900)
        let press = CGPoint(x: 300, y: 200)
        // The pointer moves 40 left and 70 down: the page shows 40 more to the right and 70 more above.
        let origin = MightyStructurePan.origin(start: start, press: press, cursor: CGPoint(x: 260, y: 270), minimum: minimum, maximum: maximum)
        #expect(origin == CGPoint(x: 140, y: 830))
        // Back where it went down: back where the page was.
        #expect(MightyStructurePan.origin(start: start, press: press, cursor: press, minimum: minimum, maximum: maximum) == start)
    }

    @Test func thePageStopsAtItsEdges() {
        let start = CGPoint(x: 20, y: 1_750)
        let press = CGPoint(x: 300, y: 300)
        let past = MightyStructurePan.origin(start: start, press: press, cursor: CGPoint(x: 600, y: -500), minimum: minimum, maximum: maximum)
        #expect(past == CGPoint(x: 0, y: 1_800))
        let other = MightyStructurePan.origin(start: start, press: press, cursor: CGPoint(x: -900, y: 4_000), minimum: minimum, maximum: maximum)
        #expect(other == CGPoint(x: 240, y: 0))
        // A page offset by its insets stops at the clip's own limits.
        let inset = MightyStructurePan.origin(start: .zero, press: press, cursor: CGPoint(x: 400, y: 400),
                                              minimum: CGPoint(x: -12, y: -8), maximum: maximum)
        #expect(inset == CGPoint(x: -12, y: -8))
    }

    @Test func anAxisThatCannotMoveKeepsItsOrigin() {
        // Narrower than the pane: only y moves. A clip with no valid range keeps its origin.
        let start = CGPoint(x: 0, y: 500)
        let origin = MightyStructurePan.origin(start: start, press: .zero, cursor: CGPoint(x: -80, y: -60),
                                               minimum: .zero, maximum: CGPoint(x: 0, y: 1_000))
        #expect(origin == CGPoint(x: 0, y: 560))
        let broken = MightyStructurePan.origin(start: CGPoint(x: 5, y: 5), press: .zero, cursor: CGPoint(x: -80, y: -60),
                                               minimum: CGPoint(x: 10, y: CGFloat.nan), maximum: CGPoint(x: 0, y: 100))
        #expect(broken == CGPoint(x: 5, y: 5))
        #expect(MightyStructurePan.scrollable(minimum: .zero, maximum: CGPoint(x: 0, y: 1_000)))
        #expect(!MightyStructurePan.scrollable(minimum: .zero, maximum: CGPoint(x: 0.2, y: 0.4)))
    }

    @Test func onlyAnUnmodifiedPressOnTheMovablePageMayPan() {
        #expect(MightyStructurePan.mayPan(.page, modified: false, scrollable: true))
        // Text selects, a scroller scrolls, a code block keeps its own scroll, and
        // the composer or another pane is not this page.
        for target in [MightyStructurePan.Target.textOrControl, .innerScroll, .outside] {
            #expect(!MightyStructurePan.mayPan(target, modified: false, scrollable: true))
        }
        // ⌃ is a right click, ⇧ and ⌘ extend selections.
        #expect(!MightyStructurePan.mayPan(.page, modified: true, scrollable: true))
        // A page that cannot move keeps every press as it was.
        #expect(!MightyStructurePan.mayPan(.page, modified: false, scrollable: false))
    }

    @Test func aShortPressIsAClickNotADrag() {
        let start = CGPoint(x: 100, y: 100)
        #expect(!MightyStructurePan.isDrag(from: start, to: start))
        #expect(!MightyStructurePan.isDrag(from: start, to: CGPoint(x: 102, y: 98)))
        #expect(MightyStructurePan.isDrag(from: start, to: CGPoint(x: 103, y: 100)))
        #expect(MightyStructurePan.isDrag(from: start, to: CGPoint(x: 100, y: 96)))
    }

    @Test func aDragPanAwayStopsFollowingAndIsNeverFought() {
        // The page follows the newest card at its end, 2_000pt tall in a 600pt pane.
        var watch = MightyGraphFollow.PageWatch()
        func page(_ origin: CGPoint, document: Double = 2_000, newest: Double = 1_992) -> MightyGraphFollow.Page {
            .init(originY: Double(origin.y), visibleHeight: 600, documentHeight: document, newestID: "b", newestBottom: newest)
        }
        let top = CGPoint(x: 0, y: 1_400)
        #expect(watch.observe(page(top), userScrolling: false) == nil)
        #expect(watch.following)
        // The user drags the page down by 200pt: it moves up, a user move.
        let range = (minimum: CGPoint.zero, maximum: CGPoint(x: 0, y: 1_400))
        let dragged = MightyStructurePan.origin(start: top, press: CGPoint(x: 50, y: 50), cursor: CGPoint(x: 50, y: 250),
                                                minimum: range.minimum, maximum: range.maximum)
        #expect(dragged.y == 1_200)
        #expect(watch.observe(page(dragged), userScrolling: true) == nil)
        #expect(!watch.following)
        // The newest card grows while the drag goes on and after it: nothing pulls the page back.
        #expect(watch.observe(page(dragged, document: 2_300, newest: 2_292), userScrolling: true) == nil)
        #expect(watch.observe(page(dragged, document: 2_400, newest: 2_392), userScrolling: false) == nil)
        // Dragged back to the newest card: following again, by the scroll rule.
        let back = MightyStructurePan.origin(start: dragged, press: CGPoint(x: 50, y: 250), cursor: CGPoint(x: 50, y: -550),
                                             minimum: .zero, maximum: CGPoint(x: 0, y: 1_800))
        #expect(back.y == 1_800)
        #expect(watch.observe(page(back, document: 2_400, newest: 2_392), userScrolling: true) == nil)
        #expect(watch.following)
    }
}
