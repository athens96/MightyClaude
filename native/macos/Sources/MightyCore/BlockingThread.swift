import Foundation

/// Runs `body` on a thread of its own, for work that blocks for as long as
/// something outside this process decides: reaping a child, a pipe's read
/// loop, a tool call that waits on the app.
///
/// Such work must not sit on the global concurrent queues. They share one
/// process-wide cap on threads (`kern.wq_max_constrained_threads`, 64 on
/// macOS); once that many block there, nothing else queued on them starts
/// until one returns. That includes the reaper that would end a child's
/// wait, so children stay unreaped while running, and every timer and
/// completion scheduled on those queues stalls with them. Each child holds
/// three such waits for its whole life, so a few dozen children at once
/// (parallel git calls, agents, probes) were enough.
func startBlockingThread(_ name: String, qualityOfService: QualityOfService = .utility, _ body: @escaping @Sendable () -> Void) {
    let thread = Thread(block: body)
    thread.name = name
    thread.qualityOfService = qualityOfService
    thread.start()
}
