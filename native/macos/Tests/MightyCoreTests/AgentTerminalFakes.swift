import Foundation
import Darwin
@testable import MightyCore

/// Fake clock: now() returns currentTime; sleep advances currentTime by the interval.
final class FakeAgentTerminalClock: AgentTerminalClock, @unchecked Sendable {
    private let lock = NSLock()
    private var current = Date(timeIntervalSince1970: 1_000_000)
    var currentTime: Date { lock.lock(); defer { lock.unlock() }; return current }
    func now() -> Date { currentTime }
    func sleep(for interval: TimeInterval) async { lock.withLock { current = current.addingTimeInterval(interval) } }
}

/// Fake terminal pane. Commands complete at once by default.
/// Set nextRunning = true before launching to simulate a long-running process.
/// Set resistsSignals = true to simulate a process that ignores SIGINT and SIGTERM,
/// and ignoresKill = true for one that SIGKILL does not end either.
final class FakeAgentTerminalPane: AgentTerminalPane, @unchecked Sendable {
    struct Entry {
        var output: String
        var code: Int32
        var running: Bool
        var signal: Int32?
    }
    private let lock = NSLock()
    private var entries: [String: Entry] = [:]

    var nextOutput: String = ""
    var nextCode: Int32 = 0
    var nextRunning: Bool = false
    var resistsSignals: Bool = false
    var ignoresKill: Bool = false
    /// Maximum ring buffer bytes (default 1 MB, matching the spec).
    var ringBufferLimit: Int = 1_048_576
    private(set) var launched: [String] = []
    private(set) var stopped: [String] = []
    private(set) var sigintCount = 0
    private(set) var sigtermCount = 0
    private(set) var sigkillCount = 0

    func launch(command: String, handle: String) async throws {
        lock.withLock {
            launched.append(command)
            entries[handle] = Entry(output: nextOutput, code: nextCode, running: nextRunning)
        }
    }

    /// Read from `fromOffset` in the total byte stream, respecting the ring buffer.
    func readOutput(handle: String, fromOffset: Int, maxBytes: Int) -> (output: String, dropped: Bool, moreRemains: Bool, nextOffset: Int) {
        lock.lock(); defer { lock.unlock() }
        guard let e = entries[handle] else { return ("", false, false, 0) }
        let allBytes = Data(e.output.utf8)
        let dropPoint = max(0, allBytes.count - ringBufferLimit)
        let effectiveOffset = max(fromOffset, dropPoint)
        let available = allBytes.dropFirst(effectiveOffset)
        let chunk = available.prefix(maxBytes)
        return (String(decoding: chunk, as: UTF8.self), dropPoint > 0, available.count > maxBytes, effectiveOffset + chunk.count)
    }

    func isRunning(handle: String) -> Bool { lock.lock(); defer { lock.unlock() }; return entries[handle]?.running ?? false }

    func exitCode(handle: String) -> Int32? {
        lock.lock(); defer { lock.unlock() }
        guard let e = entries[handle], !e.running else { return nil }
        return e.code
    }

    func terminationSignal(handle: String) -> Int32? {
        lock.lock(); defer { lock.unlock() }
        guard let e = entries[handle], !e.running else { return nil }
        return e.signal
    }

    func runningHandles() -> [String] { lock.lock(); defer { lock.unlock() }; return entries.filter { $0.value.running }.map(\.key).sorted() }

    func sendSIGINT(handle: String) { lock.lock(); sigintCount += 1; lock.unlock(); end(handle, signal: SIGINT, soft: true) }
    func sendSIGTERM(handle: String) { lock.lock(); sigtermCount += 1; lock.unlock(); end(handle, signal: SIGTERM, soft: true) }
    func sendSIGKILL(handle: String) { lock.lock(); sigkillCount += 1; lock.unlock(); end(handle, signal: SIGKILL, soft: false) }

    private func end(_ handle: String, signal: Int32, soft: Bool) {
        lock.lock(); defer { lock.unlock() }
        guard entries[handle]?.running == true, soft ? !resistsSignals : !ignoresKill else { return }
        entries[handle]?.running = false
        entries[handle]?.code = 128 + signal
        entries[handle]?.signal = signal
        stopped.append(handle)
    }

    /// Externally mark a process as finished (simulates the process exiting).
    func finish(handle: String, code: Int32) {
        lock.lock(); entries[handle]?.running = false; entries[handle]?.code = code; lock.unlock()
    }

    /// Append text to the output for `handle`: process output or PTY echo of user typing.
    func appendUserTyped(text: String, handle: String) {
        lock.lock(); entries[handle]?.output += text; lock.unlock()
    }
}

/// A binding for tests; the socket path and executable are never used by them.
func testPaneBinding(pane: String = "pane-a", token: String = "testtoken1234", workspaceId: String = "ws-1", workspacePath: String = "/tmp", provider: String = "claude", socketPath: String = "/tmp/mighty.sock") -> PaneMCPBinding {
    PaneMCPBinding(agentPaneId: pane, token: token, server: PaneMCPServerLocation(socketPath: socketPath, executable: URL(fileURLWithPath: "/Applications/MightyClaude.app/Contents/MacOS/MightyClaude")), workspaceId: workspaceId, workspacePath: workspacePath, provider: provider)
}

/// A web-open service that opens nothing and answers "in app" without asking.
func silentWebOpenService(paneRegistry: AgentIOPaneRegistry? = nil) -> WebOpenService {
    final class Presenter: WebOpenPromptPresenter, @unchecked Sendable {
        func present(url: URL, workspaceId: String) {}
        func pendingChoice() -> (destination: WebOpenDestination, remember: Bool)? { (.inApp, false) }
        func dismiss() {}
    }
    return WebOpenService(store: WebOpenChoiceStore(), presenter: Presenter(), opener: nil, paneRegistry: paneRegistry)
}

/// A fresh short temporary folder (unix socket paths must stay under 104 bytes).
func shortTemporaryDirectory() throws -> URL {
    let url = URL(fileURLWithPath: "/tmp", isDirectory: true).appendingPathComponent("mc-io-" + UUID().uuidString.prefix(8), isDirectory: true)
    try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
    // realpath, not resolvingSymlinksInPath: the latter strips /private again.
    guard let resolved = realpath(url.path, nil) else { return url }
    defer { free(resolved) }
    return URL(fileURLWithPath: String(cString: resolved), isDirectory: true)
}
