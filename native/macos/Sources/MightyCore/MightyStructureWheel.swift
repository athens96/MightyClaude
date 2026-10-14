import CoreGraphics
import Foundation

/// Wheel and trackpad scrolling of the Mac 구조 view.
///
/// Every pane sits inside the pane dock's own two-axis scroll view, and SwiftUI on
/// macOS hands a wheel over a two-axis scroll view nested in another two-axis scroll
/// view to the outer one: the dock, which has nothing to scroll, bounces and the page
/// never moves, under a trackpad and a mouse wheel alike. So the page's probe takes
/// the wheel events that land on the page itself and moves the scroll view they
/// belong to, as its drag does.
///
/// A trackpad gesture (the fingers' scroll, then its momentum) is decided once, at its
/// first movement: the innermost scroll view under the pointer that can still move that
/// way takes it, such as a code block that scrolls sideways, then the board's column
/// under the pointer, which moves up and down only, and otherwise the page, which moves
/// sideways only (`MightyStructurePan.confined`). It keeps to the axis it started along,
/// as a scroll view that scrolls along the predominant axis does. A mouse wheel's steps
/// have no gesture and are decided one by one. Either is the user's own move, so the
/// follow rule never scrolls the column under it (`MightyGraphFollow.ColumnWatch`).
public enum MightyStructureWheel {
    /// After a wheel step, or the last event of a gesture, the page is still the user's
    /// this long: its report of the last step may arrive later, and momentum starts
    /// only after the fingers lift.
    public static let settleGrace: TimeInterval = 0.3
    /// A gesture that sent nothing for this long is over, even if its end never came.
    public static let staleAfter: TimeInterval = 1.0

    /// Where a wheel event is in its gesture.
    public enum Stage: Equatable, Sendable {
        /// A mouse wheel's step: no gesture around it.
        case step
        /// Fingers touched the trackpad, or started to move.
        case began
        /// The fingers moved.
        case changed
        /// The fingers lifted; momentum may follow.
        case ended
        /// The gesture was cancelled: nothing follows.
        case cancelled
        /// The momentum after the fingers lifted.
        case momentum
        /// The momentum ran out or was stopped.
        case momentumEnded
    }

    /// The stage of a wheel event from its `NSEvent.Phase` raw values: began 1,
    /// stationary 2, changed 4, ended 8, cancelled 16, may begin 32.
    public static func stage(phase: UInt, momentumPhase: UInt) -> Stage {
        if momentumPhase != 0 { return momentumPhase & (8 | 16) != 0 ? .momentumEnded : .momentum }
        if phase == 0 { return .step }
        if phase & 16 != 0 { return .cancelled }
        if phase & 8 != 0 { return .ended }
        if phase & (1 | 32) != 0 { return .began }
        return .changed
    }

    public enum Axis: Equatable, Sendable { case horizontal, vertical }

    /// The axis a movement is mostly along; nil for no movement.
    public static func axis(of delta: CGPoint) -> Axis? {
        guard delta.x != 0 || delta.y != 0 else { return nil }
        return abs(delta.x) > abs(delta.y) ? .horizontal : .vertical
    }

    /// A scroll view's origin (top-down, as the page scrolls) after a wheel movement of
    /// `delta` points: a positive y shows what is above, a positive x what is to the left.
    /// Only `axis` moves when there is one; the origin stops at the edges, and an axis
    /// with no valid range keeps the origin it had.
    public static func origin(current: CGPoint, delta: CGPoint, axis: Axis?, minimum: CGPoint, maximum: CGPoint) -> CGPoint {
        let dx = axis == .vertical ? 0 : delta.x
        let dy = axis == .horizontal ? 0 : delta.y
        return CGPoint(x: clamp(current.x - dx, minimum.x, maximum.x, keeping: current.x),
                       y: clamp(current.y - dy, minimum.y, maximum.y, keeping: current.y))
    }

    /// The scroll view can still move the way `delta` asks.
    public static func canMove(current: CGPoint, delta: CGPoint, axis: Axis?, minimum: CGPoint, maximum: CGPoint) -> Bool {
        let next = origin(current: current, delta: delta, axis: axis, minimum: minimum, maximum: maximum)
        return abs(next.x - current.x) > 0.5 || abs(next.y - current.y) > 0.5
    }

    /// Which of the scroll views under the pointer takes a movement, innermost first and
    /// the page last: the first that can still move that way, otherwise the page.
    public static func taker(canMove: [Bool]) -> Int {
        canMove.firstIndex(of: true) ?? max(0, canMove.count - 1)
    }

    /// The page is moving under the user's hand: a scroll the system reports, a drag on
    /// the page or the moment after it (`MightyStructurePan.releaseGrace`), or a wheel.
    public static func userMoving(scrollPhase: Bool, panning: Bool, panReleasedAt: TimeInterval, wheel: Tracker, now: TimeInterval) -> Bool {
        scrollPhase || panning || now - panReleasedAt < MightyStructurePan.releaseGrace || wheel.userScrolling(at: now)
    }

    /// A page's wheel gestures, one at a time.
    public struct Tracker: Equatable, Sendable {
        /// What the probe does with a wheel event.
        public enum Action: Equatable, Sendable {
            /// Not this page's: the event goes on as it came.
            case pass
            /// This page's, with nothing to move: the event is kept from the dock.
            case hold
            /// This page's first movement (every wheel step is one): choose the scroll
            /// view that takes it (`taker`) and move it.
            case choose
            /// Move the scroll view the gesture chose.
            case move
        }

        /// The current gesture began on this page.
        private var owned = false
        private var chosen = false
        /// Fingers on the trackpad or momentum running.
        private var live = false
        private var lastEvent: TimeInterval = -.infinity
        /// The axis the current gesture keeps; nil for a wheel step, or before it moved.
        public private(set) var axis: Axis?

        public init() {}

        /// A wheel event at `now`. `overPage` says it landed on this page, as AppKit's
        /// hit test sees it (a gesture keeps the page it began on); `delta` is its movement.
        public mutating func feed(_ stage: Stage, overPage: Bool, delta: CGPoint, at now: TimeInterval) -> Action {
            let moves = delta.x != 0 || delta.y != 0
            switch stage {
            case .step:
                owned = false; chosen = false; live = false; axis = nil
                guard overPage else { return .pass }
                lastEvent = now
                return moves ? .choose : .hold
            case .began:
                owned = overPage; chosen = false; live = overPage; axis = nil
                guard owned else { return .pass }
                lastEvent = now
                guard moves else { return .hold }
                chosen = true; axis = Self.axisOf(delta)
                return .choose
            case .changed, .ended, .cancelled, .momentum, .momentumEnded:
                // Momentum starts right after the fingers lift: a gesture that ended
                // without it is over, and later momentum is another view's.
                if owned, !live, now - lastEvent >= MightyStructureWheel.settleGrace { owned = false; chosen = false }
                guard owned else { return .pass }
                lastEvent = now
                live = stage == .changed || stage == .momentum
                let finished = stage == .cancelled || stage == .momentumEnded
                defer { if finished { owned = false; chosen = false } }
                guard moves else { return .hold }
                guard chosen else {
                    chosen = true; axis = Self.axisOf(delta)
                    return .choose
                }
                return .move
            }
        }

        /// The user's wheel or fingers are moving this page: a gesture that is still
        /// going, or the moment after its last event.
        public func userScrolling(at now: TimeInterval) -> Bool {
            let since = now - lastEvent
            return (live && since < MightyStructureWheel.staleAfter) || since < MightyStructureWheel.settleGrace
        }

        private static func axisOf(_ delta: CGPoint) -> Axis? { MightyStructureWheel.axis(of: delta) }
    }

    private static func clamp(_ value: CGFloat, _ low: CGFloat, _ high: CGFloat, keeping current: CGFloat) -> CGFloat {
        guard value.isFinite, low.isFinite, high.isFinite, low <= high else { return current }
        return min(max(value, low), high)
    }
}
