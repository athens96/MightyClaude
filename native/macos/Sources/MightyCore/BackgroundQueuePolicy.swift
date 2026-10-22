import Foundation

/// How new input meets a run that is busy, decided in one place for the
/// composer and the phone (Windows Core `BackgroundQueuePolicy` is the same).
/// New input to a busy pane always waits in the queue — while the turn runs,
/// starts, is held for an update, or is over but still waits on background
/// work — and starts as the next request; nothing a person sends joins the
/// running turn. A style that starts every request in plan mode
/// (docs/mighty-styles.md §1.17.4) records plan mode on what it queues.
public enum BackgroundQueuePolicy {
    /// The queue's notice: what is queued starts only once the background work ends.
    public static func waitsOnBackground(work: BackgroundWork?, queued: Int) -> Bool {
        queued > 0 && work?.waitingOnBackground == true
    }

    /// Stop on a run whose turn is already over (it only waited on background
    /// work) keeps the queue and starts its first request next.
    public static func stopKeepsQueue(_ work: BackgroundWork?) -> Bool { work?.turnEnded == true }

    /// The permission mode a queued item records for its run.
    public static func queuedOverride(launchesInPlan: Bool) -> String? { launchesInPlan ? "plan" : nil }

    /// The permission mode a run starts with: a queued item keeps the
    /// decision recorded when it was queued; anything else starts in plan
    /// mode when the pane's style plans every request.
    public static func launchOverride(queued: QueuedInput?, launchesInPlan: Bool) -> String? {
        if let queued { return queued.permissionModeOverride }
        return launchesInPlan ? StyleLaunchPermissionMode.plan.rawValue : nil
    }
}
