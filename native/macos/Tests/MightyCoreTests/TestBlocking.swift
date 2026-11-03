import Foundation
@testable import MightyCore

// Swift Testing runs every test, synchronous or not, on the shared cooperative pool, which has one thread per core
// (three on a CI runner). A test that waits by blocking — a semaphore, a process wait, a sleep loop — holds one of
// them for as long as it waits, and every other test's `await` has to resume on the rest: with three such waits at once
// the whole suite stalls, and the tests that measure a deadline (a sign-in wait, a quit bound) fail on a runner that
// was only slow. So a test never blocks on the pool: it waits for blocking work on a thread of its own (as
// MCPPipeReader.next does), and it polls with Task.sleep.

/// Runs `work`, which may block, on a thread of its own and hands back its result.
func offThePool<T: Sendable>(_ work: @escaping @Sendable () -> T) async -> T {
    await withCheckedContinuation { continuation in
        startBlockingThread("test-blocking-wait") { continuation.resume(returning: work()) }
    }
}

/// Whether `semaphore` is signalled within `seconds`, waited for off the pool.
func signalled(_ semaphore: DispatchSemaphore, within seconds: TimeInterval) async -> Bool {
    await offThePool { semaphore.wait(timeout: .now() + seconds) == .success }
}

/// Whether `condition` holds within `seconds`, read every `interval` with Task.sleep in between.
func holds(within seconds: TimeInterval, every interval: TimeInterval = 0.02, _ condition: () -> Bool) async -> Bool {
    let deadline = Date().addingTimeInterval(seconds)
    while !condition() {
        guard Date() < deadline else { return false }
        try? await Task.sleep(for: .seconds(interval))
    }
    return true
}
