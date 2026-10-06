import Foundation
import Darwin

public struct ProcessResult: Sendable {
    public let exitCode: Int32
    public let stdout: Data
    public let stderr: Data
}

/// Foundation.Process does not promise a distinct process group. Spawn creates
/// that group before exec, so stop never races a post-launch setpgid call.
final class NativeChildProcess: @unchecked Sendable {
    private let lock = NSLock()
    private let writer = DispatchQueue(label: "dev.mightyclaude.stdin")
    private var stdinFD: Int32 = -1
    /// Set under `lock` when a close is requested, before the writer queue
    /// runs it, so `inputIsOpen` never waits on that queue.
    private var inputClosing = false
    private var stopping = false
    private var exitedAt: Date?
    private var abandoned = false
    /// After the child exits, its pipes are read for one more second. Time the
    /// consumer kept a reader waiting counts as idle only past this much.
    static let maximumDrainCredit: TimeInterval = 30
    /// At most this much is read from one pipe after the child exits.
    static let maximumBytesAfterExit = 16 * 1_048_576
    /// At most this much is read in the last look at a pipe when its drain
    /// ends on time; see `writerOutlivesDrain`.
    static let finalDrainBytes = 65_536
    /// A process outside the child's group still wrote when reading stopped
    /// at one of the limits above; the rest of its output was not read.
    var outputAbandoned: Bool { lock.lock(); defer { lock.unlock() }; return abandoned }
    private var result: Int32?
    private var waiters: [UUID: CheckedContinuation<Int32, Never>] = [:]
    private(set) var pid: pid_t = 0
    private var signal: Int32?
    /// The signal that ended the child, or nil when it exited normally or still runs.
    var terminationSignal: Int32? { lock.lock(); defer { lock.unlock() }; return signal }

    init(executable: URL, arguments: [String], environment: [String: String], cwd: URL,
         stdout: @escaping @Sendable (Data) -> Void, stderr: @escaping @Sendable (Data) -> Void,
         exited: @escaping @Sendable (Int32) -> Void) throws {
        guard executable.isFileURL, cwd.isFileURL, ([executable.path, cwd.path] + arguments + environment.map { $0.key + "=" + $0.value }).allSatisfy({ !$0.contains("\0") }) else { throw MightyError(L("process.error.badArguments")) }
        var allFDs: [Int32] = []
        func makePipe() throws -> [Int32] {
            var fds: [Int32] = [-1, -1]
            guard Darwin.pipe(&fds) == 0 else { throw MightyError(L("process.error.pipeCreate")) }
            for index in fds.indices {
                if fds[index] < 3 {
                    let replacement = fcntl(fds[index], F_DUPFD_CLOEXEC, 3)
                    Darwin.close(fds[index]); fds[index] = replacement
                }
                guard fds[index] >= 3 else { for fd in fds where fd >= 0 { Darwin.close(fd) }; throw MightyError(L("process.error.pipeOpen")) }
                _ = fcntl(fds[index], F_SETFD, FD_CLOEXEC)
            }
            allFDs += fds; return fds
        }
        var actions: posix_spawn_file_actions_t?
        var attributes: posix_spawnattr_t?
        guard posix_spawn_file_actions_init(&actions) == 0, posix_spawnattr_init(&attributes) == 0 else { throw MightyError(L("process.error.spawnAttributes")) }
        defer { posix_spawn_file_actions_destroy(&actions); posix_spawnattr_destroy(&attributes) }
        do {
            let input = try makePipe(); let output = try makePipe(); let errors = try makePipe()
            var code = posix_spawn_file_actions_adddup2(&actions, input[0], STDIN_FILENO)
            code |= posix_spawn_file_actions_adddup2(&actions, output[1], STDOUT_FILENO)
            code |= posix_spawn_file_actions_adddup2(&actions, errors[1], STDERR_FILENO)
            for fd in allFDs { code |= posix_spawn_file_actions_addclose(&actions, fd) }
            code |= cwd.path.withCString { posix_spawn_file_actions_addchdir_np(&actions, $0) }
            code |= posix_spawnattr_setpgroup(&attributes, 0)
            var emptySignals = sigset_t(); sigemptyset(&emptySignals)
            var defaultSignals = sigset_t(); sigemptyset(&defaultSignals); sigaddset(&defaultSignals, SIGPIPE)
            code |= posix_spawnattr_setsigmask(&attributes, &emptySignals)
            code |= posix_spawnattr_setsigdefault(&attributes, &defaultSignals)
            code |= posix_spawnattr_setflags(&attributes, Int16(POSIX_SPAWN_SETPGROUP | POSIX_SPAWN_SETSIGMASK | POSIX_SPAWN_SETSIGDEF | POSIX_SPAWN_CLOEXEC_DEFAULT))
            guard code == 0 else { throw MightyError(L("process.error.processGroup", ["code": String(code)])) }
            let argv = ([executable.path] + arguments).map { strdup($0) } + [nil]
            let envp = environment.map { strdup($0.key + "=" + $0.value) } + [nil]
            defer { for value in argv + envp { free(value) } }
            var childPID: pid_t = 0
            let spawned = argv.withUnsafeBufferPointer { args in envp.withUnsafeBufferPointer { env in executable.path.withCString { path in posix_spawn(&childPID, path, &actions, &attributes, args.baseAddress!, env.baseAddress!) } } }
            guard spawned == 0 else { throw MightyError(L("process.error.spawn", ["error": String(cString: strerror(spawned))])) }
            pid = childPID; stdinFD = input[1]
            Darwin.close(input[0]); Darwin.close(output[1]); Darwin.close(errors[1]); allFDs = []
            _ = fcntl(stdinFD, F_SETFL, fcntl(stdinFD, F_GETFL) | O_NONBLOCK)
            _ = fcntl(stdinFD, F_SETNOSIGPIPE, 1)
            let readers = DispatchGroup()
            read(fd: output[0], group: readers, callback: stdout)
            read(fd: errors[0], group: readers, callback: stderr)
            DispatchQueue.global(qos: .utility).async { [self] in
                var status: Int32 = 0
                while waitpid(childPID, &status, 0) < 0 { if errno != EINTR { status = 127 << 8; break } }
                lock.lock(); exitedAt = Date(); if status & 0x7f != 0 { signal = status & 0x7f }; lock.unlock()
                closeInput()
                // The parent can exit while a background child still holds a pipe.
                terminateGroup()
                let exitCode: Int32 = status & 0x7f == 0 ? (status >> 8) & 0xff : 128 + (status & 0x7f)
                readers.notify(queue: .global(qos: .utility)) { [self] in
                    lock.lock(); result = exitCode; let pending = Array(waiters.values); waiters.removeAll(); lock.unlock()
                    exited(exitCode)
                    for continuation in pending { continuation.resume(returning: exitCode) }
                }
            }
        } catch { for fd in allFDs { Darwin.close(fd) }; throw error }
    }

    private func read(fd: Int32, group: DispatchGroup, callback: @escaping @Sendable (Data) -> Void) {
        _ = fcntl(fd, F_SETFL, fcntl(fd, F_GETFL) | O_NONBLOCK)
        group.enter()
        DispatchQueue.global(qos: .utility).async { [self] in
            defer { Darwin.close(fd); group.leave() }
            var bytes = [UInt8](repeating: 0, count: 16_384)
            // Time the callback held this thread after the child exited. A
            // consumer applying backpressure is not the pipe idling, so up to
            // `maximumDrainCredit` of that wait does not count against the
            // one-second drain after exit. A writer outside the child's group
            // (a detached daemon holding the inherited pipe) keeps the consumer
            // busy forever, so the credit is capped and the bytes read after
            // exit are bounded too; past either, the pipe is closed and the
            // writer gets EPIPE.
            var handing: TimeInterval = 0
            var afterExit = 0
            while true {
                lock.lock(); let ended = exitedAt; let stopped = stopping; lock.unlock()
                if let ended {
                    let credit = stopped ? 0 : min(handing, Self.maximumDrainCredit)
                    let overBytes = afterExit > Self.maximumBytesAfterExit
                    if overBytes || Date().timeIntervalSince(ended) - credit > 1 {
                        var outlived = overBytes || handing > Self.maximumDrainCredit
                        // A stop drops the rest on purpose. Otherwise a reader
                        // slowed by a loaded machine can reach the one second
                        // before the byte cap, so look once more before telling
                        // a stray writer from the child's own unread tail.
                        if !outlived, !stopped { outlived = Self.writerOutlivesDrain(fd: fd, buffer: &bytes, deliver: callback) }
                        if outlived { lock.lock(); abandoned = true; lock.unlock() }
                        return
                    }
                }
                var descriptor = pollfd(fd: fd, events: Int16(POLLIN | POLLHUP), revents: 0)
                let ready = Darwin.poll(&descriptor, 1, 100)
                if ready < 0 { if errno == EINTR { continue }; return }
                if ready == 0 { continue }
                let count = Darwin.read(fd, &bytes, bytes.count)
                if count > 0 {
                    let began = Date()
                    if ended != nil { afterExit += count }
                    callback(Data(bytes.prefix(count)))
                    lock.lock(); let exited = exitedAt; lock.unlock()
                    if let exited { handing += max(0, Date().timeIntervalSince(max(began, exited))) }
                }
                else if count == 0 { return }
                else if errno != EAGAIN && errno != EINTR { return }
            }
        }
    }

    /// The last look at a pipe whose drain ended on time, reading at most
    /// `finalDrainBytes` without blocking. The child's whole group is gone by
    /// now, so end of file means nobody else holds the pipe: what was left is
    /// the child's own tail, handed on, and nothing was abandoned. A pipe still
    /// producing past the cap is a writer outside the group. A holder that
    /// stays silent (nothing within 50 ms of an empty read) never "kept
    /// writing" and is not reported either.
    static func writerOutlivesDrain(fd: Int32, buffer: inout [UInt8], deliver: (Data) -> Void) -> Bool {
        var drained = 0
        while drained < finalDrainBytes {
            let count = Darwin.read(fd, &buffer, min(buffer.count, finalDrainBytes - drained))
            if count > 0 { drained += count; deliver(Data(buffer.prefix(count))); continue }
            if count == 0 { return false }
            if errno == EINTR { continue }
            guard errno == EAGAIN else { return false }
            var descriptor = pollfd(fd: fd, events: Int16(POLLIN), revents: 0)
            let ready = Darwin.poll(&descriptor, 1, 50)
            if ready < 0, errno == EINTR { continue }
            if ready <= 0 { return false }
        }
        return true
    }

    func write(_ data: Data, closeAfter: Bool = false) {
        if closeAfter { lock.lock(); inputClosing = true; lock.unlock() }
        writer.async { [self] in
            guard stdinFD >= 0 else { return }
            let began = Date()
            data.withUnsafeBytes { raw in
                guard let base = raw.baseAddress else { return }
                var offset = 0
                while offset < raw.count && Date().timeIntervalSince(began) < 10 {
                    lock.lock(); let cancelled = stopping || exitedAt != nil; lock.unlock()
                    if cancelled { break }
                    let count = Darwin.write(stdinFD, base.advanced(by: offset), raw.count - offset)
                    if count > 0 { offset += count }
                    else if errno == EINTR { continue }
                    else if errno == EAGAIN { var fd = pollfd(fd: stdinFD, events: Int16(POLLOUT), revents: 0); _ = Darwin.poll(&fd, 1, 100) }
                    else { break }
                }
            }
            if closeAfter { Darwin.close(stdinFD); stdinFD = -1 }
        }
    }
    func closeInput() {
        lock.lock(); inputClosing = true; lock.unlock()
        writer.async { [self] in if stdinFD >= 0 { Darwin.close(stdinFD); stdinFD = -1 } }
    }
    /// The child itself has exited; its pipes may still be read.
    var hasExited: Bool { lock.lock(); defer { lock.unlock() }; return exitedAt != nil }
    /// Whether a write would still reach the child: no close requested (even
    /// one still queued), not stopping, not exited. Read under the lock only,
    /// never on the writer queue: a write blocked on a full stdin pipe (up to
    /// its 10 s timeout) must not stall the caller, which is the runner's actor
    /// that also drains the child's stdout, or the two would wait on each other.
    var inputIsOpen: Bool {
        lock.lock(); defer { lock.unlock() }
        return !inputClosing && !stopping && exitedAt == nil
    }
    private func terminateGroup() {
        guard pid > 0, Darwin.kill(-pid, 0) == 0 else { return }
        _ = Darwin.kill(-pid, SIGTERM)
        usleep(150_000)
        _ = Darwin.kill(-pid, SIGKILL)
    }
    /// Signals this object sent to the child's group, in order (tests read it).
    var sentSignals: [Int32] { lock.lock(); defer { lock.unlock() }; return sent }
    private var sent: [Int32] = []
    /// Signals the child's group unless the child was already reaped: its pid
    /// may then belong to another process.
    private func signalGroup(_ value: Int32) {
        lock.lock()
        guard exitedAt == nil else { lock.unlock(); return }
        sent.append(value); lock.unlock()
        _ = Darwin.kill(-pid, value)
    }
    func stop() {
        lock.lock(); let shouldStop = !stopping && result == nil; stopping = true; lock.unlock()
        guard shouldStop else { return }
        closeInput()
        signalGroup(SIGTERM)
        DispatchQueue.global(qos: .utility).asyncAfter(deadline: .now() + 0.2) { [self] in signalGroup(SIGKILL) }
    }
    /// SIGKILL to the child's group now, unless it was already reaped.
    func kill() { signalGroup(SIGKILL) }
    func wait(timeout: TimeInterval? = nil) async -> Int32 {
        await withCheckedContinuation { continuation in
            let id = UUID()
            lock.lock()
            if let result { lock.unlock(); continuation.resume(returning: result); return }
            waiters[id] = continuation; lock.unlock()
            if let timeout {
                DispatchQueue.global().asyncAfter(deadline: .now() + timeout) { [self] in
                    lock.lock(); let waiting = waiters.removeValue(forKey: id); lock.unlock()
                    if let waiting { stop(); waiting.resume(returning: -1) }
                }
            }
        }
    }
}

private final class CaptureState: @unchecked Sendable {
    let lock = NSLock()
    var stdout = Data(); var stderr = Data(); var error: Error?; var process: NativeChildProcess?
    let limit: Int
    init(limit: Int) { self.limit = limit }
    func append(_ data: Data, isError: Bool) {
        lock.lock()
        if stdout.count + stderr.count + data.count > limit {
            error = MightyError(L("process.error.outputLimit")); let child = process; lock.unlock(); child?.stop(); return
        }
        if isError { stderr.append(data) } else { stdout.append(data) }; lock.unlock()
    }
    func cancel(_ reason: Error) { lock.lock(); if error == nil { error = reason }; let child = process; lock.unlock(); child?.stop() }
    func attach(_ child: NativeChildProcess) { lock.lock(); process = child; let failed = error != nil; lock.unlock(); if failed { child.stop() } }
    func result(_ code: Int32) throws -> ProcessResult { lock.lock(); defer { lock.unlock() }; if let error { throw error }; return ProcessResult(exitCode: code, stdout: stdout, stderr: stderr) }
}

public enum ProcessCapture {
    public static func run(executable: URL, arguments: [String], environment: [String: String]? = nil, cwd: URL = FileManager.default.temporaryDirectory, input: Data? = nil, timeout: TimeInterval = 5, maximumBytes: Int = 2 * 1024 * 1024) async throws -> ProcessResult {
        let state = CaptureState(limit: maximumBytes)
        return try await withTaskCancellationHandler(operation: {
            try Task.checkCancellation()
            let process = try NativeChildProcess(executable: executable, arguments: arguments, environment: environment ?? ProcessInfo.processInfo.environment, cwd: cwd, stdout: { state.append($0, isError: false) }, stderr: { state.append($0, isError: true) }, exited: { _ in })
            state.attach(process); process.write(input ?? Data(), closeAfter: true)
            let deadline = Task {
                do { try await Task.sleep(nanoseconds: UInt64(max(0.01, timeout) * 1_000_000_000)); state.cancel(MightyError(L("process.error.timeout"))) } catch { }
            }
            let code = await process.wait(timeout: timeout + 2)
            deadline.cancel()
            return try state.result(code)
        }, onCancel: { state.cancel(CancellationError()) })
    }
}

private enum ChildEvent: Sendable { case stdout(Data), stderr(Data), exit(Int32) }

/// Bytes of unfinished lines that every run's line splitter holds, process
/// wide. Each run's splitter may grow one line up to its provider's line cap
/// (`CLIStreamParser.maximumLineBytes(provider:)`); this bounds them together.
final class ChildOutputAllowance: @unchecked Sendable {
    static let shared = ChildOutputAllowance(limit: 256 * 1_048_576)
    let limit: Int
    /// Guards every budget that shares this allowance, and this allowance.
    fileprivate let condition = NSCondition()
    fileprivate var held: [ObjectIdentifier: Int] = [:]
    fileprivate var total = 0
    init(limit: Int) { self.limit = limit }
    var heldBytes: Int { condition.lock(); defer { condition.unlock() }; return total }
    /// Past the allowance, a run holding part of a line waits unless it holds
    /// the most: that one goes on until its line ends or is cut at the line
    /// cap, so the waiting always ends. A run holding nothing still passes, so
    /// the total can exceed the allowance by about one line in progress plus
    /// each run's own budget.
    fileprivate func mustWait(_ id: ObjectIdentifier) -> Bool {
        guard total > limit, let mine = held[id] else { return false }
        return mine < (held.values.max() ?? 0)
    }
    fileprivate func set(_ id: ObjectIdentifier, _ bytes: Int) {
        total += bytes - (held[id] ?? 0)
        if bytes > 0 { held[id] = bytes } else { held.removeValue(forKey: id) }
    }
}

/// Bytes read from a child's pipes that the runner has not handed to its
/// line splitter yet. The pipe reader waits while the budget is spent, or
/// while the shared allowance for unfinished lines is (`ChildOutputAllowance`),
/// so a slow consumer makes the child block on a full pipe instead of the
/// runner dropping its output. Lines already cut are parsed and handed on at
/// once; only the unfinished one is held across chunks.
final class ChildOutputBudget: @unchecked Sendable {
    private let allowance: ChildOutputAllowance
    private var condition: NSCondition { allowance.condition }
    private let limit: Int
    private var pending = 0
    private var closed = false
    init(limit: Int, allowance: ChildOutputAllowance = .shared) { self.limit = limit; self.allowance = allowance }
    /// Blocks the reading thread until `count` more bytes fit. A chunk larger
    /// than the whole budget passes once nothing else is pending.
    func acquire(_ count: Int) {
        condition.lock(); defer { condition.unlock() }
        while !closed, (pending > 0 && pending + count > limit) || allowance.mustWait(ObjectIdentifier(self)) { condition.wait() }
        if !closed { pending += count }
    }
    /// `count` bytes were handled; the splitter now holds `held` bytes of an
    /// unfinished line.
    func release(_ count: Int, held: Int = 0) {
        condition.lock(); defer { condition.unlock() }
        pending = max(0, pending - count)
        if !closed { allowance.set(ObjectIdentifier(self), held) }
        condition.broadcast()
    }
    /// Nobody consumes any more (stopped, cancelled, gone): never wait again,
    /// and whatever the splitter held is let go.
    func close() {
        condition.lock(); defer { condition.unlock() }
        closed = true; allowance.set(ObjectIdentifier(self), 0); condition.broadcast()
    }
}

/// Each live run's child and output consumer by pane, readable without the
/// runner's actor. A busy run's consumer keeps that actor occupied, so a stop
/// that first had to get onto it could wait behind every chunk queued there;
/// signalling from here ends the child at once, whatever the caller's priority.
public final class LiveRunRegistry: @unchecked Sendable {
    private struct Entry { let child: NativeChildProcess; let consumer: Task<Void, Never>?; var signalled = false }
    private let lock = NSLock()
    private var entries: [String: Entry] = [:]
    private var isClosing = false
    /// Seconds quit waits for cleanup before the app ends regardless, killing
    /// whatever runs are left (`killAll()`).
    public static let quitDeadline: TimeInterval = 6
    public init() {}
    /// The app is quitting (`signalAll()` was called): no run starts a child
    /// any more, and one that got past that check is signalled as it registers.
    public var closing: Bool { lock.lock(); defer { lock.unlock() }; return isClosing }
    func register(_ id: String, child: NativeChildProcess, consumer: Task<Void, Never>?) {
        lock.lock(); entries[id] = Entry(child: child, consumer: consumer); let closing = isClosing; lock.unlock()
        if closing { signal(id, child: child) }
    }
    func remove(_ id: String, child: NativeChildProcess) {
        lock.lock(); if entries[id]?.child === child { entries.removeValue(forKey: id) }; lock.unlock()
    }
    /// Whether a stop was signalled for this child: its run ends "stopped".
    func wasSignalled(_ id: String, child: NativeChildProcess) -> Bool {
        lock.lock(); defer { lock.unlock() }
        return entries[id].map { $0.child === child && $0.signalled } ?? false
    }
    /// SIGTERM to the pane's process group now, SIGKILL 0.2 s later if it is
    /// still alive. A slow consumer can still have up to the run's budget
    /// queued: it gets one more second after the child exits (none when that
    /// takes over 3 s), then is cancelled and drops the rest. Repeated calls
    /// do nothing. False when the pane has no live child.
    @discardableResult public func signalStop(id: String) -> Bool { signal(id, child: nil) }
    /// Signals every live child at once, as `signalStop(id:)` does, and marks
    /// the registry closing: quit calls it first thing.
    public func signalAll() {
        lock.lock(); isClosing = true; let ids = Array(entries.keys); lock.unlock()
        for id in ids { signal(id, child: nil) }
    }
    /// SIGKILL to every live child's group now: quit's last word when its
    /// cleanup did not finish in time.
    public func killAll() {
        lock.lock(); let children = entries.values.map(\.child); lock.unlock()
        for child in children { child.kill() }
    }
    @discardableResult func signal(_ id: String, child expected: NativeChildProcess?) -> Bool {
        lock.lock()
        guard var entry = entries[id], expected == nil || entry.child === expected else { lock.unlock(); return false }
        let first = !entry.signalled
        entry.signalled = true; entries[id] = entry
        lock.unlock()
        guard first else { return true }
        entry.child.stop()
        Self.cancelConsumerAfterExit(entry.child, consumer: entry.consumer)
        return true
    }
    /// Cancels a stopped run's consumer one second after the child exits (at
    /// once when that takes over 3 s). Off every actor: the consumer keeps the
    /// runner's busy. The exit is the child's own, not its readers' end: a
    /// reader can still be held by the consumer it waits for.
    static func cancelConsumerAfterExit(_ child: NativeChildProcess, consumer: Task<Void, Never>?) {
        Task.detached(priority: .userInitiated) {
            let began = Date()
            while !child.hasExited, Date().timeIntervalSince(began) < 3 { try? await Task.sleep(for: .milliseconds(20)) }
            if child.hasExited { try? await Task.sleep(for: .seconds(1)) }
            consumer?.cancel()
        }
    }
}

private final class ManagedProcess {
    let request: StartRunRequest
    let activityId = UUID().uuidString
    var child: NativeChildProcess?
    var bridge: ModBridge?
    var parser: CLIStreamParser?
    var permissions: ClaudePermissionChannel?
    var codexPermissions: CodexApprovalChannel?
    /// At most this much read output not yet handed to the line splitter per
    /// run; see `ChildOutputBudget`.
    let outputBudget = ChildOutputBudget(limit: 4 * 1_048_576)
    var receivedClaudeResult = false
    /// The user cancelled a plan: the run ends `stopped` however the CLI ends it.
    var planCancelled = false
    /// stdin and the approval channel are closed: the CLI may exit now. While
    /// the request's own result is in but background tasks still run, both
    /// stay open, so the follow-up turns can still ask and new input can join.
    var inputClosed = false
    var lastOutputAt = Date()
    var inputWatchdog: Task<Void, Never>?
    /// Close input once the request answered and no background task runs.
    func settleInput() {
        guard receivedClaudeResult, !inputClosed, permissions != nil, parser?.backgroundRunning != true else { return }
        inputClosed = true; inputWatchdog?.cancel(); inputWatchdog = nil
        permissions?.cancelAll(); permissionInitializationTask?.cancel(); child?.closeInput()
    }
    var permissionInitializationTask: Task<Void, Never>?
    var attachments: AttachmentPreparation?
    var task: Task<Void, Never>?
    var stopping = false
    var finished = false
    var finalized = false
    var finalizationWaiters: [CheckedContinuation<Void, Never>] = []
    var outputBytes = 0
    var activityOutputBytes = 0
    var truncated = false
    let outputDecoder = UTF8StreamDecoder()
    let errorDecoder = UTF8StreamDecoder()
    let startedAt = Date()
    /// Where this Codex run writes its session records.
    var codexHome: URL?
    var codexSessions: CodexSessionWatcher?
    var codexSessionTask: Task<Void, Never>?
    init(_ request: StartRunRequest) { self.request = request }
}

public actor ProcessRunner {
    private let providerService: ProviderService
    private let pluginDirectory: URL
    private let onEvent: @Sendable (RunEvent) -> Void
    private var runs: [String: ManagedProcess] = [:]
    private var shuttingDown = false
    /// How a run launches the per-pane MCP server. Nil when the app has no
    /// agent IO socket, in which case no run is given a terminal/web binding.
    private let paneMCPServer: PaneMCPServerLocation?
    /// Live per-pane tokens, shared with the socket server that resolves them.
    /// Memory only, revoked on pane close and on quit.
    private let paneMCPBindings: PaneMCPBindingRegistry
    /// Panes this runner minted tokens for, so shutdown revokes only its own.
    private var boundPaneIds = Set<String>()
    /// Where pictures from tool results are kept; nil leaves them out.
    private let imageCache: AgentImageCache?
    /// Live children, signalled without this actor; see `LiveRunRegistry`.
    private let liveRuns: LiveRunRegistry
    /// After the last background task ends, how long a silent CLI may keep
    /// stdin before the runner closes it (a follow-up turn's own result or
    /// `session_state_changed: idle` normally closes it first).
    private let backgroundIdleClose: Double

    public init(providerService: ProviderService, pluginDirectory: URL, paneMCPServer: PaneMCPServerLocation? = nil, paneMCPBindings: PaneMCPBindingRegistry = PaneMCPBindingRegistry(), imageCache: AgentImageCache? = nil, liveRuns: LiveRunRegistry = LiveRunRegistry(), backgroundIdleClose: Double = 120, onEvent: @escaping @Sendable (RunEvent) -> Void) { self.providerService = providerService; self.pluginDirectory = pluginDirectory; self.paneMCPServer = paneMCPServer; self.paneMCPBindings = paneMCPBindings; self.imageCache = imageCache; self.liveRuns = liveRuns; self.backgroundIdleClose = backgroundIdleClose; self.onEvent = onEvent }

    /// Ends the pane's child right away, without waiting for this actor; the
    /// run's bookkeeping still needs `stop(id:)`. False when nothing ran.
    @discardableResult public nonisolated func signalStop(id: String) -> Bool { liveRuns.signalStop(id: id) }

    /// The agent pane a tool call belongs to, resolved from the token its own MCP
    /// server presented. An unknown or revoked token reaches no pane.
    public func agentPane(forPaneToken token: String) -> String? { paneMCPBindings.agentPaneId(forToken: token) }

    /// Revoke one agent pane's token. Called when that pane closes.
    public func revokePaneMCPBinding(agentPaneId: String) { paneMCPBindings.revoke(agentPaneId: agentPaneId); boundPaneIds.remove(agentPaneId) }

    public var activePaneMCPPaneIds: [String] { paneMCPBindings.activePaneIds }

    public func start(request: StartRunRequest, workspace: Workspace, allowPermissionPrompts: Bool = false) async throws {
        try Task.checkCancellation()
        try CoreValidation.validate(request)
        guard !shuttingDown else { throw MightyError(L("common.appClosing")) }
        guard runs[request.sessionId] == nil else { throw MightyError(L("run.error.paneAlreadyRunning")) }
        guard runs.count < 16 else { throw MightyError(L("run.error.concurrentLimit")) }
        guard workspace.id == request.workspaceId, StateRepository.absolutePath(workspace.path) else { throw MightyError(L("run.error.approvedWorkspaceOnly")) }
        var directory: ObjCBool = false
        guard FileManager.default.fileExists(atPath: workspace.path, isDirectory: &directory), directory.boolValue else { throw MightyError(L("workspace.error.folderMissing")) }
        let run = ManagedProcess(request); runs[request.sessionId] = run
        do {
            let snapshot = request.kind == "shell"
                ? CLIEnvironmentSnapshot(values: ProviderService.runtimeEnvironment(), source: .provided)
                : await providerService.executionEnvironment(workspacePath: workspace.path)
            try Task.checkCancellation()
            guard !run.stopping, !shuttingDown, !run.finished else { await cancelPending(run); if !request.attachments.isEmpty { throw CancellationError() }; return }
            if let detail = snapshot.fallbackDetail { emitLog(run, kind: "system", text: detail) }
            var environment = snapshot.values
            var executable: URL; var arguments: [String]
            var standardInput = Data()
            if request.kind == "shell" {
                executable = URL(fileURLWithPath: environment["SHELL"] ?? "/bin/sh"); arguments = ["-l", "-c", request.input]
            } else {
                guard let command = await providerService.command(provider: request.provider, workspacePath: workspace.path, snapshot: snapshot) else { throw MightyError(L("run.error.cliExecutableMissing", ["name": ProviderOptions.label(request.provider)])) }
                try Task.checkCancellation()
                guard !run.stopping, !shuttingDown, !run.finished else { await cancelPending(run); if !request.attachments.isEmpty { throw CancellationError() }; return }
                if request.provider == "claude", !ProviderService.supportsMods(command.version) { throw MightyError(L("run.error.modsVersionUnsupported", ["version": command.version])) }
                try CoreValidation.validateCapabilities(request, capabilities: ProviderService.capabilities(provider: request.provider, version: command.version))
                let catalog = await providerService.modelCatalog(provider: request.provider, workspacePath: workspace.path, snapshot: snapshot)
                try Task.checkCancellation()
                guard !run.stopping, !shuttingDown, !run.finished else { await cancelPending(run); if !request.attachments.isEmpty { throw CancellationError() }; return }
                try CoreValidation.validateSelection(request, catalog: catalog, registeredModels: request.registeredModels)
                if request.provider == "claude" {
                    guard FileManager.default.fileExists(atPath: pluginDirectory.appendingPathComponent(".claude-plugin/plugin.json").path) else { throw MightyError(L("run.error.modsFileMissing")) }
                    let bridge = try ModBridge(graphEnabled: true) { [weak self, weak run] metadata in guard let run else { return }; Task { await self?.receiveMod(metadata, run: run) } }
                    run.bridge = bridge
                    environment.merge(try await bridge.start()) { _, new in new }
                    try Task.checkCancellation()
                }
                guard !run.stopping, !shuttingDown, !run.finished else { await cancelPending(run); if !request.attachments.isEmpty { throw CancellationError() }; return }
                executable = command.executable
                let attachments = try AttachmentPreparation(request.attachments)
                run.attachments = attachments
                let interactivePermissions = allowPermissionPrompts && request.provider == "claude"
                let codexApprovals = request.provider == "codex" && request.settings.permissionMode == "onRequest"
                // Every Claude and Codex run gets its own MCP server with a fresh random
                // token bound to this agent pane alone. The token rides in the CLI
                // environment only, never in argv.
                let paneBinding = ["claude", "codex"].contains(request.provider) ? paneMCPServer.map {
                    paneMCPBindings.bind(agentPaneId: request.sessionId, server: $0, workspaceId: workspace.id, workspacePath: workspace.path, provider: request.provider)
                } : nil
                if let paneBinding { boundPaneIds.insert(request.sessionId); environment.merge(paneBinding.environment) { _, new in new } }
                if request.provider == "codex" {
                    let home = environment["HOME"].flatMap { $0.isEmpty ? nil : URL(fileURLWithPath: $0, isDirectory: true) } ?? FileManager.default.homeDirectoryForCurrentUser
                    run.codexHome = CLIAccountSupport.codexHome(home: home, environment: environment)
                }
                if codexApprovals {
                    arguments = try ProviderService.arguments(request, pluginDirectory: pluginDirectory, allowPermissionPrompts: allowPermissionPrompts, paneMCPBinding: paneBinding, codexHome: run.codexHome)
                } else {
                    let prepared = try ProviderInput.prepare(request, pluginDirectory: pluginDirectory, attachments: attachments, allowPermissionPrompts: interactivePermissions, paneMCPBinding: paneBinding, codexHome: run.codexHome)
                    arguments = prepared.arguments; standardInput = prepared.standardInput
                }
                if request.provider == "claude", request.settings.effort != "default" { environment["CLAUDE_CODE_EFFORT_LEVEL"] = request.settings.effort }
                if interactivePermissions {
                    // The CLI's authoritative "turn over, no background agent left" signal.
                    environment["CLAUDE_CODE_EMIT_SESSION_STATE_EVENTS"] = "1"
                    run.permissions = ClaudePermissionChannel(runId: run.activityId, prompt: standardInput,
                        write: { [weak run] data in guard let run, !run.stopping, !run.finished else { return }; run.child?.write(data) },
                        emit: { [weak run, onEvent] permission in guard let run else { return }; onEvent(RunEvent(sessionId: run.request.sessionId, type: "permission", permission: permission)) },
                        activity: { [weak run] permission, state in run?.parser?.permissionActivity(permission, state: state) },
                        warning: { [weak self, weak run] message in guard let self, let run else { return }; self.emitLogSynchronously(run, kind: "system", text: message) },
                        fail: { [weak self, weak run] message in guard let self, let run else { return }; self.emitLogSynchronously(run, kind: "error", text: message); run.child?.stop() },
                        plan: { [weak run, onEvent] record in
                            guard let run else { return }
                            // The run's graph carries this id as its sourceRunID.
                            var value = record; value.graphRunId = run.activityId
                            // A per-run plan launch: the approval leaves the pane's stored mode alone.
                            value.launchOverride = request.permissionModeOverride
                            onEvent(RunEvent(sessionId: run.request.sessionId, type: "plan", plan: value))
                        },
                        paneMode: request.settings.permissionMode)
                }
                run.parser = CLIStreamParser(provider: request.provider,
                    log: { [weak self, weak run] kind, text in guard let self, let run else { return }; self.emitLogSynchronously(run, kind: kind, text: text) },
                    resume: { [onEvent] id in onEvent(RunEvent(sessionId: request.sessionId, type: "resume", resumeId: id)) },
                    activityNamespace: run.activityId,
                    activity: { [weak self, weak run] activity in guard let self, let run else { return }; self.emitActivitySynchronously(run, activity: activity) },
                    control: { [weak run] data in guard let run, !run.stopping, !run.finished else { return }; run.permissions?.receive(data) },
                    result: { [weak run] in
                        guard let run, run.permissions != nil else { return }
                        run.receivedClaudeResult = true
                        // The editor starts a separate process for each turn.
                        // EOF only after its real result keeps approval replies
                        // possible while allowing one-shot shutdown afterwards;
                        // background tasks still running keep it open longer.
                        run.settleInput()
                    }, usage: { [weak run, onEvent] usage in
                        guard let run, !run.finished else { return }
                        onEvent(RunEvent(sessionId: run.request.sessionId, type: "usage", usage: usage))
                    }, graph: { [weak run, onEvent] node in
                        // finish() marks the process finished before flush();
                        // final graph snapshots still precede terminal status.
                        guard let run, !run.finalized else { return }
                        onEvent(RunEvent(sessionId: run.request.sessionId, type: "graph", graph: node))
                    }, graphInput: request.input,
                    images: imageCache, imageRoot: URL(fileURLWithPath: workspace.path, isDirectory: true),
                    imageEntry: { [weak run, onEvent] entry in
                        guard let run, !run.finalized else { return }
                        onEvent(RunEvent(sessionId: run.request.sessionId, type: "log", entry: entry))
                    }, todos: { [weak run, onEvent] progress in
                        guard let run, !run.finalized else { return }
                        onEvent(RunEvent(sessionId: run.request.sessionId, type: "todos", todos: progress))
                    }, background: { [weak self, weak run, onEvent] work in
                        guard let run, !run.finalized else { return }
                        onEvent(RunEvent(sessionId: run.request.sessionId, type: "background", background: work))
                        if run.receivedClaudeResult, !run.inputClosed, run.permissions != nil, work.running.isEmpty { Task { await self?.armInputWatchdog(run) } }
                    }, savedTodos: request.todoProgress, settled: { [weak run] in run?.settleInput() })
                if codexApprovals {
                    run.codexPermissions = CodexApprovalChannel(runId: run.activityId, request: request, workspacePath: workspace.path, attachments: attachments,
                        write: { [weak run] data in guard let run, !run.stopping, !run.finished else { return }; run.child?.write(data) },
                        event: { [weak run] object in guard let run, !run.finished else { return }; run.parser?.receive(object: object) },
                        emit: { [weak run, onEvent] permission in guard let run else { return }; onEvent(RunEvent(sessionId: run.request.sessionId, type: "permission", permission: permission)) },
                        activity: { [weak run] permission, state in run?.parser?.permissionActivity(permission, state: state) },
                        warning: { [weak self, weak run] message in guard let self, let run else { return }; self.emitLogSynchronously(run, kind: "system", text: message) },
                        fail: { [weak self, weak run] message in guard let self, let run else { return }; self.emitLogSynchronously(run, kind: "error", text: message); run.child?.stop() },
                        completed: { [weak run] in run?.permissionInitializationTask?.cancel(); run?.child?.closeInput() })
                }
            }
            // Quit signals the registry before it reaches this actor.
            guard !liveRuns.closing else { throw MightyError(L("common.appClosing")) }
            // Unbounded, but never more than the run's budget: the readers wait
            // for the consumer instead of evicting chunks it has not seen.
            let stream = AsyncStream<ChildEvent>.makeStream(bufferingPolicy: .unbounded)
            let continuation = stream.continuation
            let budget = run.outputBudget
            continuation.onTermination = { _ in budget.close() }
            let child = try NativeChildProcess(executable: executable, arguments: arguments, environment: environment, cwd: URL(fileURLWithPath: workspace.path), stdout: { budget.acquire($0.count); continuation.yield(.stdout($0)) }, stderr: { budget.acquire($0.count); continuation.yield(.stderr($0)) }, exited: { continuation.yield(.exit($0)); continuation.finish() })
            run.child = child
            onEvent(RunEvent(sessionId: request.sessionId, type: "status", status: "running"))
            if request.kind == "claude" { Self.deliverActivity(run, activity: AgentActivity(id: run.activityId, provider: request.provider, kind: "turn", state: "running", summary: L("run.activity.running", ["name": ProviderOptions.label(request.provider)])), emit: onEvent) }
            emitLog(run, kind: "system", text: request.kind == "shell" ? L("run.log.shellStart") : L("run.log.cliStart", ["name": ProviderOptions.label(request.provider)]))
            // A parser-driven run's stdout is cut into lines and parsed here,
            // off the actor every pane shares, with large lines' pictures
            // already decoded and cached; only finished lines hop onto it, in
            // order. A shell run's output goes through as it arrives.
            let lineLimit: Int? = run.codexPermissions != nil ? CodexApprovalChannel.maximumFrameBytes : run.parser != nil ? CLIStreamParser.maximumLineBytes(provider: request.provider) : nil
            let cache = imageCache
            // Below every user-facing caller (user-initiated): on this actor,
            // a stop, a steer or a permission answer goes ahead of the chunks
            // it queues, and a medium caller (a Task started from a plain
            // queue) is not starved either. Utility would run the parser about
            // half as fast; a stop from even lower signals first anyway.
            run.task = Task.detached(priority: .medium) { [weak self, weak run] in
                var lines = lineLimit.map { AgentOutputLines(maximumLineBytes: $0, cache: cache) }
                var exited = false
                for await event in stream.stream {
                    // Nobody handles output any more: later yields return
                    // `.terminated` and the readers never wait again.
                    guard let self, let run else { continuation.finish(); budget.close(); return }
                    // A cancelled stream still hands out what it buffered;
                    // a stop that cancels this task means drop the rest.
                    if Task.isCancelled { break }
                    switch event {
                    case .stdout(let data) where lines != nil:
                        let items = lines!.push(data)
                        if !items.isEmpty { await self.receive(items, run: run) }
                    case .exit:
                        exited = true
                        if let rest = lines?.finish(), !rest.isEmpty { await self.receive(rest, run: run) }
                        await self.receive(event, run: run)
                    default: await self.receive(event, run: run)
                    }
                    switch event { case .stdout(let data), .stderr(let data): budget.release(data.count, held: lines?.heldBytes ?? 0); case .exit: break }
                }
                // A stop cancelled this task before the exit event: still hand
                // on the unterminated last line it holds.
                budget.close()
                if !exited, let self, let run, let rest = lines?.finish(), !rest.isEmpty { await self.receive(rest, run: run) }
            }
            liveRuns.register(request.sessionId, child: child, consumer: run.task)
            if run.permissions != nil || run.codexPermissions != nil {
                run.permissions?.start(); run.codexPermissions?.start()
                run.permissionInitializationTask = Task { [weak self, weak run] in
                    do { try await Task.sleep(for: .seconds(15)) } catch { return }
                    guard let self, let run else { return }; await self.permissionInitializationTimedOut(run)
                }
            } else { child.write(standardInput, closeAfter: true) }
        } catch {
            run.permissions?.cancelAll(); run.codexPermissions?.cancelAll(); run.permissionInitializationTask?.cancel()
            run.attachments?.cleanup(); run.attachments = nil
            await run.bridge?.stop()
            if run.finished { if !request.attachments.isEmpty { throw CancellationError() }; return }
            if runs[request.sessionId] === run { runs.removeValue(forKey: request.sessionId) }
            run.parser?.finishActivities(stopped: run.stopping || shuttingDown || error is CancellationError)
            run.parser?.finishGraph(state: run.stopping || shuttingDown || error is CancellationError ? "stopped" : "error")
            if run.stopping || shuttingDown { onEvent(RunEvent(sessionId: request.sessionId, type: "status", status: "stopped")); if !request.attachments.isEmpty { throw CancellationError() }; return }
            if request.kind == "claude" { Self.deliverActivity(run, activity: AgentActivity(id: run.activityId, provider: request.provider, kind: "turn", state: "error", summary: error.localizedDescription), emit: onEvent) }
            throw error
        }
    }

    /// Delivers a follow-up while a Claude turn is still running. The CLI reads
    /// it between tool calls and answers inside the same turn. A run whose
    /// stdin frame is not open (other providers, attachments, result already
    /// received) reports false so the caller queues the text instead.
    public func steer(sessionId: String, text: String) -> Bool {
        guard let run = runs[sessionId], !run.finished, !run.stopping, !shuttingDown, run.request.provider == "claude",
              let permissions = run.permissions, permissions.initialized, !permissions.failed, !run.inputClosed,
              let child = run.child, child.inputIsOpen, let data = try? ProviderInput.claudeUserMessage(text) else { return false }
        // After the request's result this starts a new turn in the same process
        // (it stays open while background tasks run).
        run.lastOutputAt = Date()
        child.write(data)
        run.parser?.steer(id: UUID().uuidString, text: text)
        return true
    }

    /// Answers exactly one pending request in exactly one local run. A stale
    /// card from a previous execution of the same pane can never grant access.
    public func respondToPermission(sessionId: String, runId: String, requestId: String, allow: Bool) async throws {
        guard !shuttingDown, let run = runs[sessionId], run.activityId == runId,
              !run.stopping, !run.finished else {
            throw MightyError(L("run.error.permissionRunGone"))
        }
        if let permissions = run.permissions { try permissions.respond(requestId: requestId, allow: allow) }
        else if let permissions = run.codexPermissions { try permissions.respond(requestId: requestId, allow: allow) }
        else { throw MightyError(L("run.error.noInteractivePermission")) }
    }
    /// Answers one pending ExitPlanMode request in exactly one local run.
    public func answerPlan(sessionId: String, runId: String, requestId: String, decision: PlanDecision) async throws {
        guard !shuttingDown, let run = runs[sessionId], run.activityId == runId,
              !run.stopping, !run.finished, !run.inputClosed, let permissions = run.permissions, run.child?.inputIsOpen == true else {
            throw MightyError(L("plan.error.settled"))
        }
        // Marked before the reply is written, so an exit racing it still ends stopped.
        let cancelling = decision == .cancel
        if cancelling { run.planCancelled = true; run.parser?.suppressInterruptedResult() }
        do { try permissions.answerPlan(requestId: requestId, decision: decision) }
        catch { if cancelling { run.planCancelled = false; run.parser?.suppressInterruptedResult(false) }; throw error }
    }
    /// Closes input once the CLI has been silent for `backgroundIdleClose`
    /// after its last background task ended.
    private func armInputWatchdog(_ run: ManagedProcess) {
        guard !run.inputClosed, !run.finished else { return }
        run.inputWatchdog?.cancel()
        let idle = backgroundIdleClose
        run.inputWatchdog = Task { [weak self, weak run] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .milliseconds(Int(max(50, min(1_000, idle * 250)))))
                guard let self, let run, !Task.isCancelled else { return }
                if await self.inputIdle(run, after: idle) { return }
            }
        }
    }
    private func inputIdle(_ run: ManagedProcess, after idle: Double) -> Bool {
        guard !run.inputClosed, !run.finished else { return true }
        guard Date().timeIntervalSince(run.lastOutputAt) >= idle else { return false }
        run.settleInput()
        return true
    }
    public func answerUserQuestions(sessionId: String, runId: String, requestId: String, answers: [String: UserQuestionAnswer]) async throws {
        guard !shuttingDown, let run = runs[sessionId], run.activityId == runId,
              !run.stopping, !run.finished, let permissions = run.permissions else {
            throw MightyError(L("run.error.questionRunGone"))
        }
        try permissions.answerQuestions(requestId: requestId, answers: answers)
    }
    private func permissionInitializationTimedOut(_ run: ManagedProcess) {
        guard runs[run.request.sessionId] === run, !run.stopping, !run.finished else { return }
        run.permissions?.initializationTimedOut()
        run.codexPermissions?.initializationTimedOut()
    }
    // The parser is used only inside this actor's synchronous receive/flush path.
    private nonisolated func emitLogSynchronously(_ run: ManagedProcess, kind: String, text: String) { Self.deliverLog(run, kind: kind, text: text, emit: onEvent) }
    private nonisolated func emitActivitySynchronously(_ run: ManagedProcess, activity: AgentActivity) { Self.deliverActivity(run, activity: activity, emit: onEvent) }
    private static func deliverActivity(_ run: ManagedProcess, activity: AgentActivity, emit: @Sendable (RunEvent) -> Void) {
        guard !run.finalized, var value = ActivitySupport.normalized(activity) else { return }
        if let output = value.output {
            run.activityOutputBytes += output.utf8.count
            if run.activityOutputBytes > 2 * 1024 * 1024 { value.output = nil }
        }
        if value.kind != "turn" {
            // Same entry identity updates a compact tool row in place.
            let entry = LogEntry(id: value.id, kind: "system", text: value.summary, provider: value.provider, activity: value)
            emit(RunEvent(sessionId: run.request.sessionId, type: "log", entry: entry))
        }
        emit(RunEvent(sessionId: run.request.sessionId, type: "activity", activity: value))
    }
    private func emitLog(_ run: ManagedProcess, kind: String, text: String) { Self.deliverLog(run, kind: kind, text: text, emit: onEvent) }
    private static func deliverLog(_ run: ManagedProcess, kind: String, text: String, emit: @Sendable (RunEvent) -> Void) {
        guard !text.isEmpty else { return }
        if ["assistant", "output"].contains(kind) {
            guard !run.truncated else { return }
            run.outputBytes += text.utf8.count
            if run.outputBytes > 2 * 1024 * 1024 { run.truncated = true; emit(RunEvent(sessionId: run.request.sessionId, type: "log", entry: LogEntry(kind: "system", text: L("run.notice.outputOver2MB")))); return }
        }
        let clean = text.replacingOccurrences(of: "\\x1b(?:\\[[0-?]*[ -/]*[@-~]|\\][^\\x07]*(?:\\x07|\\x1b\\\\))", with: "", options: .regularExpression).unicodeScalars.filter { $0.value == 9 || $0.value == 10 || $0.value == 13 || $0.value >= 32 && $0.value != 127 }
        let value = String(String.UnicodeScalarView(clean))
        if kind == "assistant" {
            // Preserve one logical Markdown message (including code fences and
            // tables). Arbitrary 16K chunks are not separate assistant turns.
            let bounded = ActivitySupport.prefixUTF8(value, maximumBytes: 131_072)
            emit(RunEvent(sessionId: run.request.sessionId, type: "log", entry: LogEntry(kind: kind, text: bounded, provider: run.request.provider)))
            if bounded.utf8.count < value.utf8.count { emit(RunEvent(sessionId: run.request.sessionId, type: "log", entry: LogEntry(kind: "system", text: L("run.notice.messageTruncated"), provider: run.request.provider))) }
            return
        }
        var offset = value.startIndex
        while offset < value.endIndex {
            let end = value.index(offset, offsetBy: 16_384, limitedBy: value.endIndex) ?? value.endIndex
            emit(RunEvent(sessionId: run.request.sessionId, type: "log", entry: LogEntry(kind: kind, text: String(value[offset..<end]), provider: run.request.kind == "claude" ? run.request.provider : nil)))
            offset = end
        }
    }
    /// Lines `AgentOutputLines` cut and parsed off the actor.
    private func receive(_ items: [AgentOutputLines.Item], run: ManagedProcess) {
        guard !run.finished else { return }
        run.lastOutputAt = Date()
        for item in items {
            if let channel = run.codexPermissions {
                switch item {
                case .line(let line):
                    if let parser = run.parser { parser.withPreparedImages(line.images) { channel.receive(line) } } else { channel.receive(line) }
                case .tooLong: channel.frameTooLarge()
                }
            } else if let parser = run.parser {
                switch item {
                case .line(let line): parser.receive(line)
                case .tooLong: parser.lineTooLong()
                }
            }
        }
        watchCodexSessions(run)
    }
    private func receive(_ event: ChildEvent, run: ManagedProcess) async {
        guard !run.finished else { return }
        switch event {
        case .stdout(let data):
            if let permissions = run.codexPermissions { permissions.receive(data) }
            else if let parser = run.parser { parser.push(data) }
            else { emitLog(run, kind: "output", text: run.outputDecoder.push(data)) }
        case .stderr(let data):
            let text = run.errorDecoder.push(data)
            run.parser?.receiveStderr(text)
            emitLog(run, kind: "output", text: text)
        case .exit(let code): await finish(run, code: code)
        }
        watchCodexSessions(run)
    }
    /// Multi-agent v2 `codex exec` output does not name spawned threads, so
    /// once the root thread is known its children are read from the session
    /// records about once a second until the run ends. Files are read off
    /// this actor; only the snapshots come back to it.
    private func watchCodexSessions(_ run: ManagedProcess) {
        guard run.codexSessions == nil, !run.finished, let home = run.codexHome, let thread = run.parser?.codexRootThread else { return }
        let watcher = CodexSessionWatcher(codexHome: home, rootThread: thread, startedAt: run.startedAt, namespace: run.activityId)
        run.codexSessions = watcher
        run.codexSessionTask = Task.detached { [weak self, weak run] in
            await CodexSessionWatcher.drive(sleep: { try await Task.sleep(for: .seconds(1)) }) {
                let agents = watcher.poll()
                guard let self, let run else { return false }
                return await self.receiveCodexSessions(agents, run: run)
            }
        }
    }
    /// Snapshots already taken are delivered even while the run finishes;
    /// the graph ignores them once it is final.
    private func receiveCodexSessions(_ agents: [CodexSessionAgent], run: ManagedProcess) -> Bool {
        if !agents.isEmpty { run.parser?.receiveCodexSessions(agents) }
        return !run.finished
    }
    private func receiveMod(_ metadata: ModMetadata, run: ManagedProcess) {
        guard !run.finished, !run.stopping, !shuttingDown else { return }
        onEvent(RunEvent(sessionId: run.request.sessionId, type: "resume", resumeId: metadata.claudeSessionId))
        if metadata.event == "session.start" { emitLog(run, kind: "system", text: L("run.log.modsConnected")) }
        run.parser?.receiveMod(metadata)
    }
    private func cancelPending(_ run: ManagedProcess) async {
        await run.bridge?.stop()
        if run.finished { await waitForFinalization(run); return }
        await finish(run, code: -1)
    }
    private func finish(_ run: ManagedProcess, code: Int32) async {
        guard !run.finished else { return }
        // A stop signalled off the actor can end the child before `stop(id:)`
        // gets here; the run still ends "stopped".
        if let child = run.child {
            if liveRuns.wasSignalled(run.request.sessionId, child: child) { run.stopping = true }
            liveRuns.remove(run.request.sessionId, child: child)
        }
        run.codexPermissions?.flush()
        run.finished = true; run.parser?.flush(); emitLog(run, kind: "output", text: run.outputDecoder.flush()); let errorTail = run.errorDecoder.flush(); run.parser?.receiveStderr(errorTail); run.parser?.finishStderr(); emitLog(run, kind: "output", text: errorTail)
        // The run still ends by its exit code; only the stray writer's rest is lost.
        if run.child?.outputAbandoned == true, !run.stopping, !shuttingDown { emitLog(run, kind: "system", text: L("run.notice.outputAbandoned")) }
        run.permissions?.cancelAll(); run.codexPermissions?.cancelAll(); run.permissionInitializationTask?.cancel(); run.inputWatchdog?.cancel()
        let incompletePermissionRun = run.permissions != nil && (run.permissions?.initialized != true || !run.receivedClaudeResult)
        if incompletePermissionRun, !run.stopping, !shuttingDown, !run.planCancelled, run.permissions?.failed != true {
            emitLog(run, kind: "error", text: L("run.error.claudeEndedEarly"))
        }
        let incompleteCodexRun = run.codexPermissions != nil && (run.codexPermissions?.initialized != true || run.codexPermissions?.turnCompleted != true)
        if incompleteCodexRun, !run.stopping, !shuttingDown, run.codexPermissions?.failed != true {
            emitLog(run, kind: "error", text: L("run.error.codexEndedEarly"))
        }
        if let bridge = run.bridge, await bridge.receivedCount == 0, !run.stopping { emitLog(run, kind: "system", text: L("run.warning.noModsEvents")) }
        await run.bridge?.stop()
        let status = run.stopping || shuttingDown || run.planCancelled ? "stopped" : code == 0 && run.parser?.failed != true && run.permissions?.failed != true && run.codexPermissions?.failed != true && !incompletePermissionRun && !incompleteCodexRun ? "completed" : "error"
        if let watcher = run.codexSessions {
            // Stop the poll task, wait for a read in flight, then read what is
            // left once. No pause first: an exited Codex writes nothing more.
            run.codexSessionTask?.cancel()
            await run.codexSessionTask?.value
            run.parser?.receiveCodexSessions(await Task.detached { watcher.finish() }.value)
        }
        run.parser?.finishActivities(stopped: status == "stopped")
        run.parser?.finishGraph(state: status)
        run.parser?.finishBackground()
        run.attachments?.cleanup(); run.attachments = nil
        if run.request.kind == "claude" {
            let summary = status == "completed" ? L("run.activity.completed") : status == "stopped" ? L("run.activity.stopped") : L("run.activity.error")
            Self.deliverActivity(run, activity: AgentActivity(id: run.activityId, provider: run.request.provider, kind: "turn", state: status, summary: summary), emit: onEvent)
        }
        // The reason rides on the final status: only the run's last failure counts.
        let reason = status == "error" && run.parser?.authFailure == true ? "auth" : nil
        onEvent(RunEvent(sessionId: run.request.sessionId, type: "status", status: status, reason: reason))
        if runs[run.request.sessionId] === run { runs.removeValue(forKey: run.request.sessionId) }
        run.finalized = true
        let waiters = run.finalizationWaiters; run.finalizationWaiters.removeAll()
        for waiter in waiters { waiter.resume() }
    }
    private func waitForFinalization(_ run: ManagedProcess) async {
        guard !run.finalized else { return }
        await withCheckedContinuation { run.finalizationWaiters.append($0) }
    }
    public func stop(id: String) async {
        guard let run = runs[id] else { return }
        if run.finished { await waitForFinalization(run); return }
        run.stopping = true
        run.permissions?.cancelAll(); run.codexPermissions?.cancelAll(); run.permissionInitializationTask?.cancel()
        if let child = run.child {
            // Usually signalled already, before the caller reached this actor;
            // the consumer's deadline comes with the signal. Cancelled, it
            // still hands on the unterminated line its splitter holds, so wait.
            if !liveRuns.signal(id, child: child) { child.stop(); LiveRunRegistry.cancelConsumerAfterExit(child, consumer: run.task) }
            await run.task?.value
            if !run.finished { await finish(run, code: -1) } else { await waitForFinalization(run) }
        }
        else { await cancelPending(run) }
    }
    /// Signals every run first, then stops them together, so one slow run
    /// does not hold the others' bookkeeping.
    public func shutdown() async {
        shuttingDown = true
        for (id, run) in runs { if let child = run.child { liveRuns.signal(id, child: child) } }
        await withTaskGroup(of: Void.self) { group in
            for id in Array(runs.keys) { group.addTask { await self.stop(id: id) } }
        }
        for id in boundPaneIds { paneMCPBindings.revoke(agentPaneId: id) }; boundPaneIds.removeAll()
    }
}
