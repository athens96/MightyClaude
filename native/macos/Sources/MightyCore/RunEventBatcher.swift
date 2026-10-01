import Foundation

/// Run events on their way to one consumer (the main actor), in order and in
/// batches. A burst of output schedules one delivery instead of one job per
/// event, so the consumer's queue stays free for a stop or another click.
public final class RunEventBatcher: @unchecked Sendable {
    private let lock = NSLock()
    private var pending: [RunEvent] = []
    private var head = 0
    private var scheduled = false
    /// At most this many events per delivery; the rest wait for the next one,
    /// so other jobs on the consumer's queue run in between.
    public let batchLimit: Int
    public init(batchLimit: Int = 256) { self.batchLimit = max(1, batchLimit) }

    /// Queues one event. True when no delivery is scheduled yet: the caller
    /// schedules one, which calls `take()`.
    public func push(_ event: RunEvent) -> Bool {
        lock.lock(); defer { lock.unlock() }
        pending.append(event)
        guard !scheduled else { return false }
        scheduled = true
        return true
    }

    /// The oldest queued events, up to `batchLimit`, in the order they were
    /// pushed. `more` is true when events remain; the caller then schedules
    /// the next delivery itself, since `push` will not ask for one.
    public func take() -> (events: [RunEvent], more: Bool) {
        lock.lock(); defer { lock.unlock() }
        let end = min(pending.count, head + batchLimit)
        let batch = Array(pending[head..<end])
        head = end
        if head == pending.count {
            pending.removeAll(keepingCapacity: pending.count <= 4_096); head = 0; scheduled = false
            return (batch, false)
        }
        if head >= 4_096, head * 2 >= pending.count { pending.removeFirst(head); head = 0 }
        return (batch, true)
    }
}
