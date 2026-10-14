import CoreGraphics
import Foundation
import Testing
@testable import MightyCore

/// Wheel and trackpad scrolling of the Mac 구조 view: the page's probe moves the page
/// itself, since the pane dock's scroll view would take every wheel otherwise.
struct MightyStructureWheelTests {
    private typealias Wheel = MightyStructureWheel
    private let minimum = CGPoint(x: 0, y: 0)
    private let maximum = CGPoint(x: 240, y: 1_800)

    // MARK: Stages

    @Test func stagesComeFromAppKitsPhaseValues() {
        #expect(Wheel.stage(phase: 0, momentumPhase: 0) == .step)
        #expect(Wheel.stage(phase: 32, momentumPhase: 0) == .began)
        #expect(Wheel.stage(phase: 1, momentumPhase: 0) == .began)
        #expect(Wheel.stage(phase: 4, momentumPhase: 0) == .changed)
        #expect(Wheel.stage(phase: 2, momentumPhase: 0) == .changed)
        #expect(Wheel.stage(phase: 8, momentumPhase: 0) == .ended)
        #expect(Wheel.stage(phase: 16, momentumPhase: 0) == .cancelled)
        #expect(Wheel.stage(phase: 0, momentumPhase: 1) == .momentum)
        #expect(Wheel.stage(phase: 0, momentumPhase: 4) == .momentum)
        #expect(Wheel.stage(phase: 0, momentumPhase: 8) == .momentumEnded)
        #expect(Wheel.stage(phase: 0, momentumPhase: 16) == .momentumEnded)
    }

    // MARK: Moving a scroll view

    @Test func aWheelMovesTheOriginAgainstItsDelta() {
        // Fingers moving down (a positive y) show what is above; moving right shows the left.
        let start = CGPoint(x: 100, y: 900)
        #expect(Wheel.origin(current: start, delta: CGPoint(x: 0, y: 120), axis: nil, minimum: minimum, maximum: maximum) == CGPoint(x: 100, y: 780))
        #expect(Wheel.origin(current: start, delta: CGPoint(x: -60, y: 0), axis: nil, minimum: minimum, maximum: maximum) == CGPoint(x: 160, y: 900))
        #expect(Wheel.origin(current: start, delta: CGPoint(x: 30, y: -40), axis: nil, minimum: minimum, maximum: maximum) == CGPoint(x: 70, y: 940))
    }

    @Test func aGestureKeepsToItsAxis() {
        let start = CGPoint(x: 100, y: 900)
        #expect(Wheel.axis(of: CGPoint(x: -6, y: 12)) == .vertical)
        #expect(Wheel.axis(of: CGPoint(x: -12, y: 6)) == .horizontal)
        #expect(Wheel.axis(of: .zero) == nil)
        #expect(Wheel.origin(current: start, delta: CGPoint(x: -6, y: 12), axis: .vertical, minimum: minimum, maximum: maximum) == CGPoint(x: 100, y: 888))
        #expect(Wheel.origin(current: start, delta: CGPoint(x: -12, y: 6), axis: .horizontal, minimum: minimum, maximum: maximum) == CGPoint(x: 112, y: 900))
    }

    @Test func aWheelStopsAtTheEdges() {
        #expect(Wheel.origin(current: CGPoint(x: 10, y: 20), delta: CGPoint(x: 50, y: 100), axis: nil, minimum: minimum, maximum: maximum) == .zero)
        #expect(Wheel.origin(current: CGPoint(x: 230, y: 1_790), delta: CGPoint(x: -50, y: -100), axis: nil, minimum: minimum, maximum: maximum) == maximum)
        // A clip with no valid range keeps its origin.
        let broken = Wheel.origin(current: CGPoint(x: 5, y: 5), delta: CGPoint(x: 10, y: 10), axis: nil,
                                  minimum: CGPoint(x: 10, y: CGFloat.nan), maximum: CGPoint(x: 0, y: 100))
        #expect(broken == CGPoint(x: 5, y: 5))
    }

    @Test func anInnerScrollViewKeepsTheWheelOnlyWhileItCanMoveThatWay() {
        // A code block that scrolls sideways, at its left edge.
        let codeRange = (minimum: CGPoint.zero, maximum: CGPoint(x: 400, y: 0))
        let atLeft = CGPoint.zero
        let right = Wheel.canMove(current: atLeft, delta: CGPoint(x: -20, y: 0), axis: .horizontal, minimum: codeRange.minimum, maximum: codeRange.maximum)
        let left = Wheel.canMove(current: atLeft, delta: CGPoint(x: 20, y: 0), axis: .horizontal, minimum: codeRange.minimum, maximum: codeRange.maximum)
        let down = Wheel.canMove(current: atLeft, delta: CGPoint(x: 0, y: -20), axis: .vertical, minimum: codeRange.minimum, maximum: codeRange.maximum)
        #expect(right)
        #expect(!left)
        #expect(!down)
        // Innermost first, the page last: the code block takes a sideways scroll it can
        // make, and the page takes the rest.
        #expect(Wheel.taker(canMove: [right, true]) == 0)
        #expect(Wheel.taker(canMove: [left, true]) == 1)
        #expect(Wheel.taker(canMove: [down, true]) == 1)
        // A page at its edge still takes it, so the dock never does.
        #expect(Wheel.taker(canMove: [false, false]) == 1)
        #expect(Wheel.taker(canMove: [false]) == 0)
    }

    // MARK: Gestures

    @Test func aTrackpadGestureOnThePageIsDecidedOnceAndKeptThroughItsMomentum() {
        var wheel = Wheel.Tracker()
        #expect(wheel.feed(.began, overPage: true, delta: .zero, at: 10) == .hold)
        #expect(wheel.axis == nil)
        #expect(wheel.feed(.changed, overPage: false, delta: CGPoint(x: -3, y: 12), at: 10.02) == .choose)
        #expect(wheel.axis == .vertical)
        #expect(wheel.feed(.changed, overPage: false, delta: CGPoint(x: -8, y: 4), at: 10.04) == .move)
        #expect(wheel.axis == .vertical)
        #expect(wheel.feed(.ended, overPage: false, delta: .zero, at: 10.06) == .hold)
        #expect(wheel.feed(.momentum, overPage: false, delta: CGPoint(x: 0, y: 9), at: 10.1) == .move)
        #expect(wheel.feed(.momentumEnded, overPage: false, delta: .zero, at: 10.5) == .hold)
        // The gesture is over: momentum from elsewhere is not this page's.
        #expect(wheel.feed(.momentum, overPage: false, delta: CGPoint(x: 0, y: 9), at: 11) == .pass)
    }

    @Test func aGestureThatBeganElsewhereIsNeverThisPagesEvenOverIt() {
        var wheel = Wheel.Tracker()
        #expect(wheel.feed(.began, overPage: false, delta: .zero, at: 10) == .pass)
        #expect(wheel.feed(.changed, overPage: true, delta: CGPoint(x: 0, y: 12), at: 10.02) == .pass)
        #expect(wheel.feed(.ended, overPage: true, delta: .zero, at: 10.04) == .pass)
        #expect(wheel.feed(.momentum, overPage: true, delta: CGPoint(x: 0, y: 8), at: 10.06) == .pass)
        #expect(!wheel.userScrolling(at: 10.06))
    }

    @Test func aGestureThatMovesAtOnceChoosesAtItsStart() {
        var wheel = Wheel.Tracker()
        #expect(wheel.feed(.began, overPage: true, delta: CGPoint(x: -14, y: 2), at: 10) == .choose)
        #expect(wheel.axis == .horizontal)
        #expect(wheel.feed(.changed, overPage: true, delta: CGPoint(x: -14, y: 2), at: 10.02) == .move)
    }

    @Test func aMouseWheelStepIsDecidedOnItsOwn() {
        var wheel = Wheel.Tracker()
        #expect(wheel.feed(.step, overPage: true, delta: CGPoint(x: 0, y: -30), at: 10) == .choose)
        #expect(wheel.axis == nil)
        #expect(wheel.feed(.step, overPage: true, delta: CGPoint(x: 0, y: -30), at: 10.05) == .choose)
        #expect(wheel.feed(.step, overPage: false, delta: CGPoint(x: 0, y: -30), at: 10.1) == .pass)
        // A step ends any gesture before it: its stray momentum is not this page's.
        #expect(wheel.feed(.momentum, overPage: true, delta: CGPoint(x: 0, y: 8), at: 10.15) == .pass)
    }

    @Test func aNewGestureElsewhereEndsAGestureThisPageStillHeld() {
        var wheel = Wheel.Tracker()
        _ = wheel.feed(.began, overPage: true, delta: .zero, at: 10)
        _ = wheel.feed(.changed, overPage: true, delta: CGPoint(x: 0, y: 12), at: 10.02)
        _ = wheel.feed(.ended, overPage: true, delta: .zero, at: 10.04)
        // No momentum came; the next gesture is on another pane.
        #expect(wheel.feed(.began, overPage: false, delta: .zero, at: 12) == .pass)
        #expect(wheel.feed(.momentum, overPage: false, delta: CGPoint(x: 0, y: 8), at: 12.2) == .pass)
    }

    @Test func momentumLongAfterAGestureEndedIsNotThisPages() {
        // A gesture on the page ended with no momentum; the next one began on the
        // diagram, whose probe kept its began from this page.
        var wheel = Wheel.Tracker()
        _ = wheel.feed(.began, overPage: true, delta: .zero, at: 10)
        _ = wheel.feed(.changed, overPage: true, delta: CGPoint(x: 0, y: 12), at: 10.02)
        _ = wheel.feed(.ended, overPage: true, delta: .zero, at: 10.04)
        #expect(wheel.feed(.momentum, overPage: false, delta: CGPoint(x: 0, y: 8), at: 10.04 + Wheel.settleGrace + 0.2) == .pass)
        #expect(wheel.feed(.momentumEnded, overPage: false, delta: .zero, at: 10.04 + Wheel.settleGrace + 0.4) == .pass)
    }

    // MARK: The user's scroll

    @Test func aGestureIsTheUsersFromItsStartUntilJustAfterItsLastEvent() {
        var wheel = Wheel.Tracker()
        #expect(!wheel.userScrolling(at: 10))
        _ = wheel.feed(.began, overPage: true, delta: .zero, at: 10)
        // Fingers resting before they move: already the user's.
        #expect(wheel.userScrolling(at: 10.5))
        _ = wheel.feed(.changed, overPage: true, delta: CGPoint(x: 0, y: 12), at: 10.6)
        _ = wheel.feed(.ended, overPage: true, delta: .zero, at: 10.7)
        // Between the lift and the momentum.
        #expect(wheel.userScrolling(at: 10.7 + Wheel.settleGrace - 0.01))
        _ = wheel.feed(.momentum, overPage: true, delta: CGPoint(x: 0, y: 9), at: 10.8)
        #expect(wheel.userScrolling(at: 11.4))
        _ = wheel.feed(.momentumEnded, overPage: true, delta: .zero, at: 11.5)
        #expect(wheel.userScrolling(at: 11.5 + Wheel.settleGrace - 0.01))
        #expect(!wheel.userScrolling(at: 11.5 + Wheel.settleGrace + 0.01))
    }

    @Test func aMouseWheelStepIsTheUsersForAMoment() {
        var wheel = Wheel.Tracker()
        _ = wheel.feed(.step, overPage: true, delta: CGPoint(x: 0, y: -30), at: 10)
        #expect(wheel.userScrolling(at: 10 + Wheel.settleGrace - 0.01))
        #expect(!wheel.userScrolling(at: 10 + Wheel.settleGrace + 0.01))
    }

    @Test func aGestureWhoseEndNeverCameGoesStale() {
        var wheel = Wheel.Tracker()
        _ = wheel.feed(.began, overPage: true, delta: .zero, at: 10)
        _ = wheel.feed(.changed, overPage: true, delta: CGPoint(x: 0, y: 12), at: 10.1)
        #expect(wheel.userScrolling(at: 10.1 + Wheel.staleAfter - 0.01))
        #expect(!wheel.userScrolling(at: 10.1 + Wheel.staleAfter + 0.01))
    }

    @Test func theUserMovesThePageByWheelDragOrScrollBar() {
        let idle = Wheel.Tracker()
        #expect(!Wheel.userMoving(scrollPhase: false, panning: false, panReleasedAt: 0, wheel: idle, now: 100))
        #expect(Wheel.userMoving(scrollPhase: true, panning: false, panReleasedAt: 0, wheel: idle, now: 100))
        #expect(Wheel.userMoving(scrollPhase: false, panning: true, panReleasedAt: 0, wheel: idle, now: 100))
        #expect(Wheel.userMoving(scrollPhase: false, panning: false, panReleasedAt: 100 - MightyStructurePan.releaseGrace + 0.01, wheel: idle, now: 100))
        var wheel = Wheel.Tracker()
        _ = wheel.feed(.began, overPage: true, delta: .zero, at: 99.9)
        #expect(Wheel.userMoving(scrollPhase: false, panning: false, panReleasedAt: 0, wheel: wheel, now: 100))
    }

    @Test func followNeverScrollsWhileAWheelGestureIsGoing() {
        // Symptom: a trackpad scroll on the 구조 page never reached the system's scroll
        // phase, so a page that grew under it was read as a layout and followed back.
        var watch = MightyGraphFollow.PageWatch()
        func page(_ origin: Double, _ document: Double) -> MightyGraphFollow.Page {
            .init(originY: origin, visibleHeight: 600, documentHeight: document, newestID: "b", newestBottom: document - 8)
        }
        #expect(watch.observe(page(1_400, 2_000), userScrolling: false) == nil)
        #expect(watch.following)
        var wheel = Wheel.Tracker()
        _ = wheel.feed(.began, overPage: true, delta: .zero, at: 10)
        _ = wheel.feed(.changed, overPage: true, delta: CGPoint(x: 0, y: 20), at: 10.02)
        // The page moved up 20pt and grew by 300pt in the same report.
        let moving = Wheel.userMoving(scrollPhase: false, panning: false, panReleasedAt: 0, wheel: wheel, now: 10.03)
        #expect(moving)
        #expect(watch.observe(page(1_380, 2_300), userScrolling: moving) == nil)
        #expect(!watch.following)
        // Without the wheel known, the same report pulled the reader back down.
        var blind = MightyGraphFollow.PageWatch()
        _ = blind.observe(page(1_400, 2_000), userScrolling: false)
        #expect(blind.observe(page(1_380, 2_300), userScrolling: false) == .bottom)
    }
}
