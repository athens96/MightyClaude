import CoreGraphics
import Testing
@testable import MightyCore

/// Blocks and the pet's bubble resize from every side the user grabs.
@Suite struct ResizeEdgesTests {
    let frame = CGRect(x: 100, y: 100, width: 300, height: 200)

    @Test func theBandReachesFurtherOutsideThanInside() {
        func at(_ x: CGFloat, _ y: CGFloat) -> ResizeEdges { ResizeEdges.at(CGPoint(x: x, y: y), frame: frame, outside: 5, inside: 2) }
        #expect(at(97, 200) == .left)
        #expect(at(403, 200) == .right)
        #expect(at(250, 101) == .top)
        #expect(at(250, 298) == .bottom)
        #expect(at(101, 101) == [.left, .top])
        // Inside the thin inner strip belongs to the box's own scrollers and text.
        #expect(at(104, 200).isEmpty)
        #expect(at(250, 200).isEmpty)
        #expect(at(90, 200).isEmpty)
    }

    @Test func everySideGrowsAwayFromTheBoxAndHonoursZoomAndBounds() {
        let minimum = CGSize(width: 300, height: 140), maximum = CGSize(width: 1400, height: 1200)
        let initial = CGSize(width: 360, height: 240)
        #expect(ResizeEdges.bottomRight.size(from: initial, delta: CGSize(width: 90, height: 60), zoom: 1.5, minimum: minimum, maximum: maximum) == CGSize(width: 420, height: 280))
        #expect(ResizeEdges.left.size(from: initial, delta: CGSize(width: -40, height: 99), minimum: minimum, maximum: maximum) == CGSize(width: 400, height: 240))
        #expect(ResizeEdges.top.size(from: initial, delta: CGSize(width: 99, height: -30), minimum: minimum, maximum: maximum) == CGSize(width: 360, height: 270))
        #expect(ResizeEdges([.left, .top]).size(from: initial, delta: CGSize(width: 4000, height: 4000), minimum: minimum, maximum: maximum) == minimum)
        #expect(ResizeEdges.right.size(from: initial, delta: CGSize(width: 9000, height: 0), minimum: minimum, maximum: maximum).width == 1400)
    }

    @Test func theCornerOppositeTheDraggedSidesStaysPut() {
        #expect(ResizeEdges.bottomRight.pinnedUnit == CGPoint(x: 0, y: 0))
        #expect(ResizeEdges.left.pinnedUnit == CGPoint(x: 1, y: 0))
        #expect(ResizeEdges([.left, .top]).pinnedUnit == CGPoint(x: 1, y: 1))
    }

    @Test func thePetWindowFitsItsBubble() {
        #expect(CompanionBubbleLayout.panelSize(width: nil, height: nil, tall: false) == CGSize(width: 282, height: 330))
        #expect(CompanionBubbleLayout.panelSize(width: nil, height: nil, tall: true) == CGSize(width: 282, height: 494))
        #expect(CompanionBubbleLayout.panelSize(width: 400, height: 300, tall: false) == CGSize(width: 424, height: 453))
        #expect(CompanionBubbleLayout.panelSize(width: 9000, height: 9000, tall: false) == CGSize(width: 664, height: 633))
        // The approval bubble keeps its own height; only the width follows.
        #expect(CompanionBubbleLayout.panelSize(width: 400, height: 300, tall: true) == CGSize(width: 424, height: 494))
        // A narrow bubble gets a narrow window; the approval never goes below the default.
        #expect(CompanionBubbleLayout.panelSize(width: 220, height: nil, tall: false) == CGSize(width: 244, height: 330))
        #expect(CompanionBubbleLayout.panelSize(width: 220, height: nil, tall: true) == CGSize(width: 282, height: 494))
    }
}
