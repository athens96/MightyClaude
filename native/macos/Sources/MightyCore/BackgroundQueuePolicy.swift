import Foundation

/// How new input meets a run that is busy, decided in one place for the
/// composer and the phone (Windows Core `BackgroundQueuePolicy` is the same).
/// A turn that is over but still runs background work takes new input in the
/// same process — except in a style that starts every request in plan mode
/// (docs/mighty-styles.md §1.17.4), whose new request waits and then starts
/// fresh in plan mode.
public enum BackgroundQueuePolicy {
    /// Enter (or ⌘Enter, `steering`) in the composer joins the running turn.
    public static func composerJoins(steering: Bool, work: BackgroundWork?, launchesInPlan: Bool) -> Bool {
        steering || (work?.waitingOnBackground == true && !launchesInPlan)
    }

    /// The phone's send steers unless it asked to queue — and never into a
    /// turn that only waits on background work in a plan-mode style.
    public static func phoneSteers(mode: String?, work: BackgroundWork?, launchesInPlan: Bool) -> Bool {
        mode != "queue" && !(work?.waitingOnBackground == true && launchesInPlan)
    }

    /// The queue's notice: what is queued starts only once the background work ends.
    public static func waitsOnBackground(work: BackgroundWork?, launchesInPlan: Bool, queued: Int) -> Bool {
        launchesInPlan && queued > 0 && work?.waitingOnBackground == true
    }

    /// Stop on a run whose turn is already over (it only waited on background
    /// work) keeps the queue and starts its first request next.
    public static func stopKeepsQueue(_ work: BackgroundWork?) -> Bool { work?.turnEnded == true }

    /// The permission mode a queued item records for its run.
    public static func queuedOverride(launchesInPlan: Bool) -> String? { launchesInPlan ? "plan" : nil }
}
