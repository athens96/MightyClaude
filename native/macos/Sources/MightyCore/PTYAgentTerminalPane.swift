import Foundation
import Darwin

/// The agent pane's one visible terminal pane. Each command runs under its own
/// PTY, so programs see a tty and colours and prompts work. The command runs
/// through the user's shell in the run's workspace folder as the leader of its
/// own session and process group, so stop signals reach the whole group and
/// Ctrl+C typed into the pane reaches it through the tty.
///
/// Per handle: the PTY output (process output plus the tty's echo of what the
/// user typed) is kept in a buffer capped at ``ringBufferMaxBytes`` (1 MB).
/// ``readOutput(handle:fromOffset:maxBytes:)`` reports `dropped == true` once
/// older bytes are evicted, uses `totalWritten` to place the caller's offset
/// after eviction, and never splits a UTF-8 character.
///
/// For the view: every command's output, each preceded by a `$ <command>`
/// header line, forms one combined stream. ``subscribe(_:)`` replays the most
/// recent ``transcriptMaxBytes`` of it and then delivers new bytes in order.
public final class PTYAgentTerminalPane: AgentTerminalPane, @unchecked Sendable {
    /// Maximum bytes retained per handle (1 MB).
    public static let ringBufferMaxBytes: Int = 1_048_576
    /// Maximum bytes of the combined stream replayed to a new subscriber.
    public static let transcriptMaxBytes: Int = 1_048_576

    public let workingDirectory: URL
    private let environment: [String: String]
    private let launched: @Sendable (PTYAgentTerminalPane) -> Void
    private let lock = NSLock()
    /// All PTY reads, writes, resizes and closes run here, in order.
    private let io = DispatchQueue(label: "dev.mightyclaude.agent-terminal.io")
    /// Output is handed to subscribers here, in the order it was produced.
    private let delivery = DispatchQueue(label: "dev.mightyclaude.agent-terminal.view")
    private var entries: [String: Entry] = [:]
    private var launchOrder: [String] = []
    private var transcript = Data()
    private var subscribers: [UUID: @Sendable (Data) -> Void] = [:]
    private var size = winsize(ws_row: 24, ws_col: 80, ws_xpixel: 0, ws_ypixel: 0)

    private struct Entry {
        var pid: pid_t
        var master: Int32
        var reader: DispatchSourceRead?
        /// Retained bytes — the last `ringBufferMaxBytes` of the handle's output.
        var buffer = Data()
        /// Total bytes ever appended; `totalWritten - buffer.count` is the index of buffer[0].
        var totalWritten = 0
        var running = true
        var exitCode: Int32?
        var signal: Int32?
    }

    /// `launched` is told after each command starts, so the app can show the pane.
    public init(workingDirectory: URL, environment: [String: String] = ProviderService.runtimeEnvironment(), launched: @escaping @Sendable (PTYAgentTerminalPane) -> Void = { _ in }) {
        self.workingDirectory = workingDirectory
        self.launched = launched
        var environment = environment
        environment["TERM"] = "xterm-256color"
        environment["COLORTERM"] = "truecolor"
        self.environment = environment
    }

    // MARK: - AgentTerminalPane

    public func launch(command: String, handle: String) async throws {
        let shell = environment["SHELL"].flatMap { $0.hasPrefix("/") ? $0 : nil } ?? "/bin/sh"
        let arguments = [shell, "-c", command]
        let variables = environment.map { $0.key + "=" + $0.value }
        guard workingDirectory.isFileURL, (arguments + variables + [workingDirectory.path]).allSatisfy({ !$0.contains("\0") }) else { throw MightyError("The command or its environment contains a NUL byte.") }
        var isDirectory: ObjCBool = false
        guard FileManager.default.fileExists(atPath: workingDirectory.path, isDirectory: &isDirectory), isDirectory.boolValue else { throw MightyError("The workspace folder \(workingDirectory.path) does not exist.") }
        // Everything the child touches is prepared here: between fork and exec
        // it only makes system calls on these pointers.
        let argv = arguments.map { strdup($0) } + [nil]
        let envp = variables.map { strdup($0) } + [nil]
        let cwd = strdup(workingDirectory.path)
        defer { for value in argv + envp + [cwd] { free(value) } }
        var master: Int32 = -1
        var initial = lock.withLock { size }
        // forkpty makes the child a session leader whose controlling tty is the
        // PTY, so its group is the tty's foreground group: typed Ctrl+C reaches it.
        // A posix_spawn'd session leader does not get the PTY as its controlling tty.
        // Signals stay blocked across the fork until the child has reset their
        // handlers, so a stop or Ctrl+C that arrives early ends it rather than
        // running a handler it inherited from the app.
        var blocked = sigset_t(), previous = sigset_t(), none = sigset_t()
        sigfillset(&blocked); sigemptyset(&none)
        _ = pthread_sigmask(SIG_SETMASK, &blocked, &previous)
        let pid = argv.withUnsafeBufferPointer { args in envp.withUnsafeBufferPointer { env in
            let pid = forkpty(&master, nil, nil, &initial)
            guard pid == 0 else { return pid }
            var mode = termios()
            if tcgetattr(STDIN_FILENO, &mode) == 0 { mode.c_iflag |= tcflag_t(IUTF8); _ = tcsetattr(STDIN_FILENO, TCSANOW, &mode) }
            _ = signal(SIGPIPE, SIG_DFL); _ = signal(SIGINT, SIG_DFL); _ = signal(SIGTERM, SIG_DFL); _ = signal(SIGHUP, SIG_DFL)
            _ = signal(SIGQUIT, SIG_DFL); _ = signal(SIGCHLD, SIG_DFL); _ = signal(SIGTSTP, SIG_DFL); _ = signal(SIGTTIN, SIG_DFL); _ = signal(SIGTTOU, SIG_DFL)
            _ = sigprocmask(SIG_SETMASK, &none, nil)
            let limit = min(getdtablesize(), 65_536)
            var fd: Int32 = 3
            while fd < limit { _ = Darwin.close(fd); fd += 1 }
            if chdir(cwd) == 0 { _ = execve(args[0], args.baseAddress!, env.baseAddress!) }
            let failure: StaticString = "MightyClaude could not start the command in the workspace folder.\r\n"
            _ = Darwin.write(STDERR_FILENO, failure.utf8Start, failure.utf8CodeUnitCount)
            _exit(127)
        } }
        let forkError = errno
        _ = pthread_sigmask(SIG_SETMASK, &previous, nil)
        guard pid > 0 else { throw MightyError("Could not open a terminal (PTY) for the command: \(String(cString: strerror(forkError))).") }
        _ = fcntl(master, F_SETFD, FD_CLOEXEC)
        _ = fcntl(master, F_SETFL, fcntl(master, F_GETFL) | O_NONBLOCK)
        // Return once the child leads the tty's foreground group, so a stop or
        // Ctrl+C sent right away reaches it rather than a group not made yet.
        for _ in 0..<2_000 where tcgetpgrp(master) != pid { usleep(500) }

        let reader = DispatchSource.makeReadSource(fileDescriptor: master, queue: io)
        reader.setEventHandler { [weak self] in self?.drain(handle) }
        reader.setCancelHandler { Darwin.close(master) }
        lock.withLock {
            entries[handle] = Entry(pid: pid, master: master, reader: reader)
            launchOrder.append(handle)
            emit(Self.header(for: command, after: transcript.last))
        }
        reader.resume()
        DispatchQueue.global(qos: .utility).async { [weak self] in
            var status: Int32 = 0
            while waitpid(pid, &status, 0) < 0 { if errno != EINTR { status = 127 << 8; break } }
            self?.io.async { self?.exited(handle, status: status) }
        }
        launched(self)
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

    /// Writes what the user typed to `handle`'s PTY. The tty's echo then records
    /// it in that handle's output exactly as the terminal shows it, so the agent
    /// reads it back once, and input the program hides (a password) stays hidden.
    public func appendUserTyped(text: String, handle: String) {
        let data = Data(text.utf8)
        guard !data.isEmpty else { return }
        io.async { [self] in
            let began = Date()
            var offset = 0
            while offset < data.count, Date().timeIntervalSince(began) < 2 {
                guard let fd = lock.withLock({ entries[handle].flatMap { $0.running && $0.master >= 0 ? $0.master : nil } }) else { return }
                let count = data.withUnsafeBytes { Darwin.write(fd, $0.baseAddress!.advanced(by: offset), data.count - offset) }
                if count > 0 { offset += count }
                else if errno == EAGAIN { var ready = pollfd(fd: fd, events: Int16(POLLOUT), revents: 0); _ = Darwin.poll(&ready, 1, 50) }
                else if errno != EINTR { return }
            }
        }
    }

    // MARK: - View

    /// The command that keystrokes from the view go to. The rule: the most
    /// recently launched command that is still running. When it exits, input
    /// falls back to the next most recent running one; with none running,
    /// keystrokes are dropped.
    public var inputHandle: String? {
        lock.lock(); defer { lock.unlock() }
        return launchOrder.last { entries[$0]?.running == true }
    }

    /// Keystrokes and pastes from the view, delivered to ``inputHandle``.
    public func sendUserInput(_ text: String) {
        guard let handle = inputHandle else { return }
        appendUserTyped(text: text, handle: handle)
    }

    /// The view's grid size; running commands get SIGWINCH and later ones start at this size.
    public func resize(columns: UInt16, rows: UInt16) {
        guard columns > 0, rows > 0 else { return }
        io.async { [self] in
            let (masters, next): ([Int32], winsize) = lock.withLock {
                size = winsize(ws_row: rows, ws_col: columns, ws_xpixel: 0, ws_ypixel: 0)
                return (entries.values.filter { $0.running && $0.master >= 0 }.map(\.master), size)
            }
            var value = next
            for master in masters { _ = ioctl(master, TIOCSWINSZ, &value) }
        }
    }

    /// Replay the retained combined stream to `receiver`, then deliver every new
    /// byte, in order, on a private serial queue. Returns the id to unsubscribe with.
    @discardableResult
    public func subscribe(_ receiver: @escaping @Sendable (Data) -> Void) -> UUID {
        lock.lock(); defer { lock.unlock() }
        let id = UUID()
        subscribers[id] = receiver
        let replay = transcript
        if !replay.isEmpty { delivery.async { receiver(replay) } }
        return id
    }

    public func unsubscribe(_ id: UUID) {
        lock.lock(); defer { lock.unlock() }
        subscribers.removeValue(forKey: id)
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

    /// `$ <command>` in dim text on its own line. Control characters are shown
    /// as `?` so a command cannot drive the terminal through its own header.
    static func header(for command: String, after last: UInt8?) -> Data {
        let visible = command.unicodeScalars.map { $0 == "\n" ? "\r\n" : $0.value < 0x20 || $0.value == 0x7F ? "?" : String($0) }.joined()
        return Data(((last == nil || last == UInt8(ascii: "\n") ? "" : "\r\n") + "\u{1B}[2m$ " + visible + "\u{1B}[0m\r\n").utf8)
    }

    // MARK: - Private helpers

    private func signalGroup(handle: String, _ signal: Int32) {
        lock.lock(); let pid = entries[handle].flatMap { $0.running ? $0.pid : nil } ?? 0; lock.unlock()
        guard pid > 0 else { return }
        _ = Darwin.kill(-pid, signal)
    }

    /// Read everything the PTY has now. Runs on `io`.
    private func drain(_ handle: String) {
        guard let fd = lock.withLock({ entries[handle]?.master }), fd >= 0 else { return }
        var bytes = [UInt8](repeating: 0, count: 16_384)
        while true {
            let count = Darwin.read(fd, &bytes, bytes.count)
            if count > 0 { append(Data(bytes.prefix(count)), handle: handle); continue }
            if count < 0, errno == EINTR { continue }
            if count < 0, errno == EAGAIN { return }
            // End of file or EIO: every holder of the slave side closed it.
            closeMaster(handle); return
        }
    }

    /// Runs on `io` once the command is reaped: take its last output, then mark it ended.
    private func exited(_ handle: String, status: Int32) {
        drain(handle)
        closeMaster(handle)
        lock.withLock {
            guard entries[handle] != nil else { return }
            entries[handle]!.running = false
            entries[handle]!.exitCode = status & 0x7f == 0 ? (status >> 8) & 0xff : 128 + (status & 0x7f)
            entries[handle]!.signal = status & 0x7f == 0 ? nil : status & 0x7f
        }
    }

    private func closeMaster(_ handle: String) {
        // Cancelling closes the descriptor on `io`, after any read already under way.
        let reader: DispatchSourceRead? = lock.withLock {
            let reader = entries[handle]?.reader
            entries[handle]?.reader = nil; entries[handle]?.master = -1
            return reader
        }
        reader?.cancel()
    }

    private func append(_ data: Data, handle: String) {
        lock.lock(); defer { lock.unlock() }
        guard entries[handle] != nil else { return }
        entries[handle]!.buffer.append(data)
        entries[handle]!.totalWritten += data.count
        let excess = entries[handle]!.buffer.count - Self.ringBufferMaxBytes
        if excess > 0 { entries[handle]!.buffer.removeFirst(excess) }
        emit(data)
    }

    /// Add to the combined stream and hand it to subscribers. Called with `lock` held,
    /// so bytes reach every subscriber in the order they were produced.
    private func emit(_ data: Data) {
        transcript.append(data)
        let excess = transcript.count - Self.transcriptMaxBytes
        if excess > 0 { transcript.removeFirst(excess) }
        let receivers = Array(subscribers.values)
        guard !receivers.isEmpty else { return }
        delivery.async { for receiver in receivers { receiver(data) } }
    }
}
