import Foundation

/// Which agent the desktop pet's bubble shows when several are busy. The pet
/// follows the most urgent agent on its own; once the user pages to another,
/// that one stays until it stops being active.
public enum CompanionCarousel {
    /// The user's pick while it is still active, otherwise the automatic choice.
    public static func shown(pinned: String?, active: [String], fallback: String?) -> String? {
        if let pinned, active.contains(pinned) { return pinned }
        return fallback
    }

    /// The neighbour `offset` pages away, wrapping at both ends. Nil when there is nothing to page through.
    public static func step(from shown: String?, in active: [String], by offset: Int) -> String? {
        guard active.count > 1, offset != 0 else { return nil }
        guard let shown, let index = active.firstIndex(of: shown) else { return offset > 0 ? active.first : active.last }
        let count = active.count
        return active[((index + offset) % count + count) % count]
    }

    /// 1-based position for the "2 / 3" label; nil when the shown agent is not one of the active ones.
    public static func position(of shown: String?, in active: [String]) -> Int? {
        shown.flatMap(active.firstIndex(of:)).map { $0 + 1 }
    }
}

/// Turns a stream of horizontal scroll deltas into single page turns: one per
/// trackpad gesture, and at most one per cooldown for wheels without phases.
public struct SwipePager: Sendable {
    public var threshold: Double
    public var cooldown: TimeInterval
    private var horizontal = 0.0, vertical = 0.0
    private var fired = false
    private var lastFire: TimeInterval = -.infinity
    private var lastFeed: TimeInterval = -.infinity
    /// A gesture that went quiet this long is over, even if its end was never seen
    /// (the pointer left the pet mid-swipe).
    static let idleReset: TimeInterval = 0.25

    public init(threshold: Double = 36, cooldown: TimeInterval = 0.35) {
        self.threshold = threshold
        self.cooldown = cooldown
    }

    public enum Phase: Sendable { case began, changed, ended, none }

    /// +1 for the next page, -1 for the previous one, 0 otherwise. It follows the
    /// system's scroll direction like any scrolling content: a negative `deltaX`
    /// (fingers moving left with natural scrolling) brings the next page in.
    public mutating func feed(deltaX: Double, deltaY: Double, phase: Phase, at time: TimeInterval) -> Int {
        if time - lastFeed > Self.idleReset { horizontal = 0; vertical = 0; fired = false }
        lastFeed = time
        switch phase {
        case .began: horizontal = 0; vertical = 0; fired = false
        case .ended: horizontal = 0; vertical = 0; fired = false; return 0
        case .none:
            // A plain wheel has no gesture to bound it: every burst starts over.
            if time - lastFire > cooldown { horizontal = 0; vertical = 0; fired = false }
        case .changed: break
        }
        horizontal += deltaX
        vertical += abs(deltaY)   // an up-then-down wiggle is still a vertical gesture
        guard !fired, abs(horizontal) >= threshold, abs(horizontal) > vertical, time - lastFire >= cooldown else { return 0 }
        fired = true
        lastFire = time
        return horizontal < 0 ? 1 : -1
    }
}
