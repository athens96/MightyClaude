import Foundation

/// Result returned to the agent from a `run_in_terminal`, `read_latest_output` or `stop` call.
public struct TerminalRunResult: Sendable, Equatable {
    public enum Status: String, Sendable, Equatable {
        case done    // process exited; exitCode is set
        case running // still running; use handle to poll or stop
    }
    public let handle: String
    public let status: Status
    /// Combined output and user-typed text since the previous read, up to readChunkBytes.
    public let output: String
    /// Set iff status == .done.
    public let exitCode: Int32?
    /// The signal that ended the process, when a signal ended it.
    public let signal: Int32?
    /// Older bytes were dropped from the ring buffer.
    public let outputDropped: Bool
    /// More unread bytes remain in the ring buffer.
    public let moreRemains: Bool

    public init(handle: String, status: Status, output: String, exitCode: Int32?, signal: Int32? = nil,
                outputDropped: Bool = false, moreRemains: Bool = false) {
        self.handle = handle; self.status = status; self.output = output; self.exitCode = exitCode
        self.signal = signal; self.outputDropped = outputDropped; self.moreRemains = moreRemains
    }
}

/// Injectable clock so tests drive timing without real sleep.
public protocol AgentTerminalClock: Sendable {
    func now() -> Date
    func sleep(for interval: TimeInterval) async
}

public struct SystemAgentTerminalClock: AgentTerminalClock, Sendable {
    public init() {}
    public func now() -> Date { Date() }
    public func sleep(for interval: TimeInterval) async {
        try? await Task.sleep(nanoseconds: UInt64(max(0, interval) * 1_000_000_000))
    }
}

/// The one terminal pane owned by an agent pane. Stage 1 runs a headless
/// ``SubprocessAgentTerminalPane``; the visible terminal pane conforms to the same
/// protocol. In automated tests a fake is used instead.
public protocol AgentTerminalPane: AnyObject, Sendable {
    /// Launch `command` in the terminal pane and tag it with `handle`.
    func launch(command: String, handle: String) async throws

    /// Return combined output starting at `fromOffset` (capped at `maxBytes`),
    /// plus ring-buffer flags: `dropped` if older bytes were evicted,
    /// `moreRemains` if more unread bytes follow, and `nextOffset` to pass on
    /// the subsequent call so each read advances past what was already returned.
    func readOutput(handle: String, fromOffset: Int, maxBytes: Int) -> (output: String, dropped: Bool, moreRemains: Bool, nextOffset: Int)

    /// Whether the process is still running.
    func isRunning(handle: String) -> Bool

    /// Exit code once exited; nil while still running.
    func exitCode(handle: String) -> Int32?

    /// The signal that ended the process; nil while running or after a normal exit.
    func terminationSignal(handle: String) -> Int32?

    /// Handles whose processes are still running.
    func runningHandles() -> [String]

    /// Send SIGINT to the process group (first stop signal).
    func sendSIGINT(handle: String)

    /// Send SIGTERM to the process group (escalation after SIGINT is ignored).
    func sendSIGTERM(handle: String)

    /// Send SIGKILL to the process group (final escalation). Does not wait.
    func sendSIGKILL(handle: String)

    /// Append `text` to the output buffer for `handle`, exactly as PTY echo
    /// does when the user types into the terminal. The next `readOutput` call
    /// returns the appended bytes alongside any process output.
    func appendUserTyped(text: String, handle: String)
}

/// Handles `run_in_terminal`, `read_latest_output`, and `stop` for one agent pane.
///
/// One instance per agent pane lives in ``AgentProcessRegistry`` until the app
/// quits. The pane is created on first use and reused for every later command.
/// A handle is known only to the runner that launched it, so another pane's
/// handle is rejected.
public actor AgentTerminalRunner {
    /// How long `runInTerminal` waits before returning a still-running result.
    public static let initialWaitSeconds: TimeInterval = 12
    /// Maximum bytes returned per read call (64 KB).
    public static let readChunkBytes: Int = 65_536
    /// Seconds between sending SIGINT and SIGTERM during stop escalation.
    public static let sigintToSIGTERMSeconds: TimeInterval = 3
    /// Seconds between sending SIGTERM and SIGKILL during stop escalation.
    public static let sigtermToSIGKILLSeconds: TimeInterval = 5
    /// Stop returns by this time after it began, whatever the process does,
    /// so the result reaches the agent within 10 seconds.
    public static let stopDeadlineSeconds: TimeInterval = 9.5
    /// Poll interval while waiting for a process to finish.
    static let pollInterval: TimeInterval = 0.05

    private let pane: AgentTerminalPane
    private let clock: AgentTerminalClock
    private var knownHandles: Set<String> = []
    /// Per-handle read cursors: byte offset of the next unread byte.
    private var readOffsets: [String: Int] = [:]

    public init(pane: AgentTerminalPane, clock: AgentTerminalClock = SystemAgentTerminalClock()) {
        self.pane = pane
        self.clock = clock
    }

    public nonisolated var terminalPane: AgentTerminalPane { pane }

    /// Run `command` in the terminal pane.
    ///
    /// If the process exits within `initialWaitSeconds` the result has
    /// `status: .done`, the full output, and the exit code.
    /// Otherwise the result has `status: .running`, partial output, no exit
    /// code, and a `handle` for follow-up `readLatestOutput` or `stop` calls.
    public func runInTerminal(command: String) async throws -> TerminalRunResult {
        let handle = UUID().uuidString
        try await pane.launch(command: command, handle: handle)
        knownHandles.insert(handle)
        readOffsets[handle] = 0
        _ = await waitForExit(handle: handle, until: clock.now().addingTimeInterval(Self.initialWaitSeconds))
        return readAndAdvance(handle: handle)
    }

    /// Read the output produced since the last read for this handle (at most
    /// `readChunkBytes` per call). Returns nil if `handle` is unknown.
    public func readLatestOutput(handle: String) -> TerminalRunResult? {
        guard knownHandles.contains(handle) else { return nil }
        return readAndAdvance(handle: handle)
    }

    /// Stop a running process with SIGINT escalating to SIGTERM (after 3 s) and
    /// SIGKILL (after 5 more s) when the process ignores softer signals. Each
    /// stage ends early once the process exits, and the result is returned no
    /// later than `stopDeadlineSeconds` after the call began (clock time).
    /// Returns nil if `handle` is unknown.
    public func stop(handle: String) async -> TerminalRunResult? {
        guard knownHandles.contains(handle) else { return nil }
        let began = clock.now()
        if pane.isRunning(handle: handle) {
            pane.sendSIGINT(handle: handle)
            if await !waitForExit(handle: handle, until: began.addingTimeInterval(Self.sigintToSIGTERMSeconds)) {
                pane.sendSIGTERM(handle: handle)
                if await !waitForExit(handle: handle, until: began.addingTimeInterval(Self.sigintToSIGTERMSeconds + Self.sigtermToSIGKILLSeconds)) {
                    pane.sendSIGKILL(handle: handle)
                    _ = await waitForExit(handle: handle, until: began.addingTimeInterval(Self.stopDeadlineSeconds))
                }
            }
        }
        return readAndAdvance(handle: handle)
    }

    /// Poll until the process exits or `deadline` passes; true once it has exited.
    private func waitForExit(handle: String, until deadline: Date) async -> Bool {
        while pane.isRunning(handle: handle) {
            guard clock.now() < deadline else { return false }
            await clock.sleep(for: Self.pollInterval)
        }
        return true
    }

    /// Read from the current cursor position, advance the cursor, and return a result.
    private func readAndAdvance(handle: String) -> TerminalRunResult {
        let running = pane.isRunning(handle: handle)
        let offset = readOffsets[handle] ?? 0
        let (output, dropped, more, next) = pane.readOutput(handle: handle, fromOffset: offset, maxBytes: Self.readChunkBytes)
        readOffsets[handle] = next
        return TerminalRunResult(
            handle: handle,
            status: running ? .running : .done,
            output: output,
            exitCode: running ? nil : pane.exitCode(handle: handle),
            signal: running ? nil : pane.terminationSignal(handle: handle),
            outputDropped: dropped,
            moreRemains: more
        )
    }
}
