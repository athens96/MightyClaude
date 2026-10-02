import Foundation

/// The scripted reference scene the measurement runs against: a short
/// count-in, 60 s of motion (a document scrolling beside a terminal being typed
/// into) and 30 s of a screen that does not change at all. The same scene
/// every time, so numbers from two runs, two networks or two builds compare.
///
/// The timing lives here, free of any window, so the app's view and the tests
/// read the same clock. The app plays it in a window of its own: nothing the
/// user has open is scrolled, typed into or touched.
public enum ScreenShareReferenceScene {
    public static let prerollSeconds: TimeInterval = 3
    public static let motionSeconds: TimeInterval = 60
    public static let stillSeconds: TimeInterval = 30
    public static var totalSeconds: TimeInterval { prerollSeconds + motionSeconds + stillSeconds }

    /// Document scroll speed during motion, in points per second.
    public static let scrollPointsPerSecond: Double = 90
    /// Terminal typing speed during motion.
    public static let typedCharactersPerSecond: Double = 18

    public enum Phase: String, Sendable, Equatable, CaseIterable {
        /// The count-in: the window is up and nothing moves yet.
        case preroll
        /// Scrolling and typing.
        case motion
        /// Nothing changes on screen, not even a ticking clock.
        case still
        /// Over (or stopped early).
        case done
    }

    /// Where the scene stands `elapsed` seconds after it started.
    public struct Moment: Sendable, Equatable {
        public var phase: Phase
        /// Whole seconds left in this phase, rounded up — what a countdown shows.
        /// Zero once the scene is done.
        public var secondsLeft: Int
        /// Seconds of motion played so far, clamped to the motion phase: what
        /// drives the scroll and the typing, and why both freeze in `still`.
        public var motionElapsed: TimeInterval

        public init(phase: Phase, secondsLeft: Int, motionElapsed: TimeInterval) {
            self.phase = phase; self.secondsLeft = secondsLeft; self.motionElapsed = motionElapsed
        }
    }

    /// When a phase begins, in seconds from the start.
    public static func start(of phase: Phase) -> TimeInterval {
        switch phase {
        case .preroll: return 0
        case .motion: return prerollSeconds
        case .still: return prerollSeconds + motionSeconds
        case .done: return totalSeconds
        }
    }

    public static func moment(at elapsed: TimeInterval) -> Moment {
        let t = elapsed.isFinite ? max(0, elapsed) : 0
        let motionElapsed = min(motionSeconds, max(0, t - prerollSeconds))
        let phase: Phase
        if t < start(of: .motion) { phase = .preroll }
        else if t < start(of: .still) { phase = .motion }
        else if t < start(of: .done) { phase = .still }
        else { phase = .done }
        let end: TimeInterval
        switch phase {
        case .preroll: end = start(of: .motion)
        case .motion: end = start(of: .still)
        case .still: end = start(of: .done)
        case .done: end = t
        }
        return Moment(phase: phase, secondsLeft: Int((end - t).rounded(.up)), motionElapsed: motionElapsed)
    }

    /// How far the document has scrolled, wrapped to `contentHeight` so a long
    /// motion phase loops the page instead of running off its end.
    public static func scrollOffset(motionElapsed: TimeInterval, contentHeight: Double) -> Double {
        let offset = max(0, motionElapsed) * scrollPointsPerSecond
        guard contentHeight > 0 else { return 0 }
        return offset.truncatingRemainder(dividingBy: contentHeight)
    }

    /// How many characters of the terminal script are on screen.
    public static func typedCount(motionElapsed: TimeInterval) -> Int {
        Int((max(0, motionElapsed) * typedCharactersPerSecond).rounded(.down))
    }

    /// The data-channel note sent to every connected phone as each phase starts,
    /// so a measurement export can mark its readings by phase (docs/relay.md).
    public static func announcement(_ phase: Phase) -> [String: Any] {
        ["t": "scene", "phase": phase.rawValue]
    }
}
