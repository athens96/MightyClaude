import Testing
@testable import MightyCore

struct CompanionCarouselTests {
    @Test func theUsersPickHoldsWhileItIsActive() {
        let active = ["a", "b", "c"]
        #expect(CompanionCarousel.shown(pinned: nil, active: active, fallback: "c") == "c")
        #expect(CompanionCarousel.shown(pinned: "b", active: active, fallback: "c") == "b")
        // Finished agents leave the carousel and the pet goes back to choosing on its own.
        #expect(CompanionCarousel.shown(pinned: "b", active: ["a", "c"], fallback: "a") == "a")
        #expect(CompanionCarousel.shown(pinned: "b", active: [], fallback: nil) == nil)
    }

    @Test func pagingWrapsAndNeedsSomethingToPageThrough() {
        let active = ["a", "b", "c"]
        #expect(CompanionCarousel.step(from: "a", in: active, by: 1) == "b")
        #expect(CompanionCarousel.step(from: "c", in: active, by: 1) == "a")
        #expect(CompanionCarousel.step(from: "a", in: active, by: -1) == "c")
        #expect(CompanionCarousel.step(from: "a", in: active, by: -4) == "c")
        // The bubble may be showing a finished agent: paging enters the active list at the matching end.
        #expect(CompanionCarousel.step(from: "done", in: active, by: 1) == "a" && CompanionCarousel.step(from: nil, in: active, by: -1) == "c")
        #expect(CompanionCarousel.step(from: "a", in: ["a"], by: 1) == nil && CompanionCarousel.step(from: "a", in: [], by: 1) == nil)
        #expect(CompanionCarousel.step(from: "a", in: active, by: 0) == nil)
        #expect(CompanionCarousel.position(of: "b", in: active) == 2 && CompanionCarousel.position(of: "done", in: active) == nil)
    }

    @Test func oneTrackpadGestureTurnsOnePage() {
        var pager = SwipePager(threshold: 30, cooldown: 0.3)
        var turns: [Int] = []
        turns.append(pager.feed(deltaX: -10, deltaY: 0, phase: .began, at: 0))
        turns.append(pager.feed(deltaX: -15, deltaY: 1, phase: .changed, at: 0.02))
        turns.append(pager.feed(deltaX: -15, deltaY: 0, phase: .changed, at: 0.04))
        turns.append(pager.feed(deltaX: -80, deltaY: 0, phase: .changed, at: 0.06))
        turns.append(pager.feed(deltaX: 0, deltaY: 0, phase: .ended, at: 0.08))
        #expect(turns == [0, 0, 1, 0, 0])
        // The next gesture counts again, in the other direction, once the cooldown has passed.
        let early = pager.feed(deltaX: 40, deltaY: 0, phase: .began, at: 0.1)
        let later = pager.feed(deltaX: 40, deltaY: 0, phase: .began, at: 0.5)
        #expect(early == 0 && later == -1)
    }

    @Test func verticalScrollsAndWheelBurstsDoNotFlipPages() {
        var pager = SwipePager(threshold: 30, cooldown: 0.3)
        let began = pager.feed(deltaX: -20, deltaY: 60, phase: .began, at: 0)
        let changed = pager.feed(deltaX: -20, deltaY: 60, phase: .changed, at: 0.02)
        #expect(began == 0 && changed == 0)
        // Down then up nets zero but was still a vertical gesture.
        var wiggle = SwipePager(threshold: 30, cooldown: 0.3)
        let down = wiggle.feed(deltaX: -20, deltaY: 50, phase: .began, at: 5)
        let up = wiggle.feed(deltaX: -20, deltaY: -50, phase: .changed, at: 5.02)
        #expect(down == 0 && up == 0)
        // A gesture whose end was never seen does not leak into the next one.
        var stale = SwipePager(threshold: 30, cooldown: 0.3)
        let half = stale.feed(deltaX: -25, deltaY: 0, phase: .changed, at: 10)
        let muchLater = stale.feed(deltaX: -10, deltaY: 0, phase: .changed, at: 11)
        #expect(half == 0 && muchLater == 0)
        var wheel = SwipePager(threshold: 3, cooldown: 0.3)
        let first = wheel.feed(deltaX: 4, deltaY: 0, phase: .none, at: 1)
        let burst = wheel.feed(deltaX: 4, deltaY: 0, phase: .none, at: 1.1)
        let next = wheel.feed(deltaX: -4, deltaY: 0, phase: .none, at: 1.6)
        #expect(first == -1 && burst == 0 && next == 1)
    }
}
