import Foundation
import Darwin

/// Headless conformer of ``AgentTerminalPane`` that runs each command in its own
/// process group with POSIX spawn (backed by ``NativeChildProcess``).
///
/// One instance owns the single terminal pane of one agent pane. Commands run
/// through the user's shell in the run's workspace folder, never the app's own
/// working directory. The visible terminal pane replaces this type behind the
/// pane factory the agent IO handler is given.
///
/// Ring buffer: combined stdout, stderr and user-typed text is appended to a
/// per-handle buffer capped at ``ringBufferMaxBytes`` (1 MB). Older bytes are
/// evicted when the cap is reached; ``readOutput(handle:fromOffset:maxBytes:)``
/// reports `dropped == true` and uses `totalWritten` to place the caller's
/// offset correctly even after eviction. Reads never split a UTF-8 character.
public final class SubprocessAgentTerminalPane: AgentTerminalPane, @unchecked Sendable {
    /// Maximum bytes retained in the ring buffer per handle (1 MB).
    public static let ringBufferMaxBytes: Int = 1_048_576

    public let workingDirectory: URL
    private let environment: [String: String]
    private let lock = NSLock()
    private var entries: [String: Entry] = [:]

    private struct Entry {
        var process: NativeChildProcess?
        /// Retained bytes — the last `ringBufferMaxBytes` of combined output.
        var buffer: Data
        /// Total bytes ever appended; `totalWritten - buffer.count` is the index of buffer[0].
        var totalWritten: Int
        var running: Bool
        var exitCode: Int32?
        var signal: Int32?
    }

    public init(workingDirectory: URL, environment: [String: String] = ProviderService.runtimeEnvironment()) {
        self.workingDirectory = workingDirectory
        self.environment = environment
    }

    // MARK: - AgentTerminalPane

    public func launch(command: String, handle: String) async throws {
        let shell = environment["SHELL"].flatMap { $0.hasPrefix("/") ? $0 : nil } ?? "/bin/sh"
        // Registered before the spawn so no early output or exit is lost.
        lock.withLock { entries[handle] = Entry(process: nil, buffer: Data(), totalWritten: 0, running: true) }
        do {
            let process = try NativeChildProcess(
                executable: URL(fileURLWithPath: shell),
                arguments: ["-c", command],
                environment: environment,
                cwd: workingDirectory,
                stdout: { [weak self] data in self?.append(data, handle: handle) },
                stderr: { [weak self] data in self?.append(data, handle: handle) },
                exited: { [weak self] code in self?.markExited(handle: handle, code: code) }
            )
            lock.withLock {
                entries[handle]?.process = process
                if entries[handle]?.running == false { entries[handle]?.signal = process.terminationSignal }
            }
        } catch {
            _ = lock.withLock { entries.removeValue(forKey: handle) }
            throw error
        }
    }

    public func readOutput(handle: String, fromOffset: Int, maxBytes: Int) -> (output: String, dropped: Bool, moreRemains: Bool, nextOffset: Int) {
        lock.lock(); defer { lock.unlock() }
        guard let entry = entries[handle] else { return ("", false, false, 0) }
        let retained = entry.buffer
        // The absolute byte index of the first retained byte.
        let dropPoint = entry.totalWritten - retained.count
        let dropped = dropPoint > 0
        var start = max(fromOffset, dropPoint) - dropPoint
        // Eviction may have cut a character; never return its orphaned tail bytes.
        if fromOffset < dropPoint { while start < retained.count, Self.isContinuation(retained[retained.startIndex + start]) { start += 1 } }
        let available = retained.dropFirst(start)
        let length = Self.utf8SafeLength(available, limit: maxBytes, holdIncompleteTail: entry.running)
        let chunk = available.prefix(length)
        return (String(decoding: chunk, as: UTF8.self), dropped, available.count > maxBytes, dropPoint + start + length)
    }

    public func isRunning(handle: String) -> Bool {
        lock.lock(); defer { lock.unlock() }
        return entries[handle]?.running ?? false
    }

    public func exitCode(handle: String) -> Int32? {
        lock.lock(); defer { lock.unlock() }
        guard let entry = entries[handle], !entry.running else { return nil }
        return entry.exitCode
    }

    public func terminationSignal(handle: String) -> Int32? {
        lock.lock(); defer { lock.unlock() }
        guard let entry = entries[handle], !entry.running else { return nil }
        return entry.signal
    }

    public func runningHandles() -> [String] {
        lock.lock(); defer { lock.unlock() }
        return entries.filter { $0.value.running }.map(\.key).sorted()
    }

    public func sendSIGINT(handle: String) { signalGroup(handle: handle, SIGINT) }
    public func sendSIGTERM(handle: String) { signalGroup(handle: handle, SIGTERM) }
    public func sendSIGKILL(handle: String) { signalGroup(handle: handle, SIGKILL) }

    public func appendUserTyped(text: String, handle: String) {
        append(Data(text.utf8), handle: handle)
    }

    // MARK: - UTF-8 boundaries

    static func isContinuation(_ byte: UInt8) -> Bool { byte & 0xC0 == 0x80 }

    /// The longest prefix of `bytes` of at most `limit` bytes that does not end
    /// inside a multibyte character. A cut character is left for the next read.
    /// An incomplete sequence at the very end is also held back while the
    /// process may still write its remaining bytes.
    static func utf8SafeLength<C: Collection>(_ bytes: C, limit: Int, holdIncompleteTail: Bool) -> Int where C.Element == UInt8, C.Index == Int {
        let count = bytes.count
        guard count > 0, limit > 0 else { return 0 }
        let cut = min(count, limit)
        guard cut < count || holdIncompleteTail else { return cut }
        // Find the lead byte of the last character that starts before `cut`.
        var lead = cut - 1, steps = 0
        while lead > 0, steps < 3, isContinuation(bytes[bytes.startIndex + lead]) { lead -= 1; steps += 1 }
        let byte = bytes[bytes.startIndex + lead]
        let width = byte < 0x80 ? 1 : byte >> 5 == 0b110 ? 2 : byte >> 4 == 0b1110 ? 3 : byte >> 3 == 0b11110 ? 4 : 1
        // Invalid or complete sequences pass through unchanged.
        guard !isContinuation(byte), lead + width > cut else { return cut }
        // A lone cut character in a tiny limit is returned whole rather than stalling reads.
        return lead == 0 && cut < count ? cut : lead
    }

    // MARK: - Private helpers

    private func signalGroup(handle: String, _ signal: Int32) {
        lock.lock(); let pid = entries[handle]?.process?.pid ?? 0; lock.unlock()
        guard pid > 0 else { return }
        _ = Darwin.kill(-pid, signal)
    }

    private func append(_ data: Data, handle: String) {
        lock.lock(); defer { lock.unlock() }
        guard entries[handle] != nil else { return }
        entries[handle]!.buffer.append(data)
        entries[handle]!.totalWritten += data.count
        let excess = entries[handle]!.buffer.count - Self.ringBufferMaxBytes
        if excess > 0 { entries[handle]!.buffer.removeFirst(excess) }
    }

    private func markExited(handle: String, code: Int32) {
        lock.lock(); defer { lock.unlock() }
        guard entries[handle] != nil else { return }
        entries[handle]!.running = false
        entries[handle]!.exitCode = code
        entries[handle]!.signal = entries[handle]!.process?.terminationSignal
    }
}
