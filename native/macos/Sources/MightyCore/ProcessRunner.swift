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
    private var stopping = false
    private var exitedAt: Date?
    private var result: Int32?
    private var waiters: [UUID: CheckedContinuation<Int32, Never>] = [:]
    private(set) var pid: pid_t = 0

    init(executable: URL, arguments: [String], environment: [String: String], cwd: URL,
         stdout: @escaping @Sendable (Data) -> Void, stderr: @escaping @Sendable (Data) -> Void,
         exited: @escaping @Sendable (Int32) -> Void) throws {
        guard executable.isFileURL, cwd.isFileURL, ([executable.path, cwd.path] + arguments + environment.map { $0.key + "=" + $0.value }).allSatisfy({ !$0.contains("\0") }) else { throw MightyError("프로세스 실행 경로 또는 인자가 올바르지 않습니다.") }
        var allFDs: [Int32] = []
        func makePipe() throws -> [Int32] {
            var fds: [Int32] = [-1, -1]
            guard Darwin.pipe(&fds) == 0 else { throw MightyError("프로세스 파이프를 만들지 못했습니다.") }
            for index in fds.indices {
                if fds[index] < 3 {
                    let replacement = fcntl(fds[index], F_DUPFD_CLOEXEC, 3)
                    Darwin.close(fds[index]); fds[index] = replacement
                }
                guard fds[index] >= 3 else { for fd in fds where fd >= 0 { Darwin.close(fd) }; throw MightyError("프로세스 파이프를 열지 못했습니다.") }
                _ = fcntl(fds[index], F_SETFD, FD_CLOEXEC)
            }
            allFDs += fds; return fds
        }
        var actions: posix_spawn_file_actions_t?
        var attributes: posix_spawnattr_t?
        guard posix_spawn_file_actions_init(&actions) == 0, posix_spawnattr_init(&attributes) == 0 else { throw MightyError("프로세스 실행 속성을 만들지 못했습니다.") }
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
            guard code == 0 else { throw MightyError("안전한 프로세스 그룹을 만들지 못했습니다 (\(code)).") }
            let argv = ([executable.path] + arguments).map { strdup($0) } + [nil]
            let envp = environment.map { strdup($0.key + "=" + $0.value) } + [nil]
            defer { for value in argv + envp { free(value) } }
            var childPID: pid_t = 0
            let spawned = argv.withUnsafeBufferPointer { args in envp.withUnsafeBufferPointer { env in executable.path.withCString { path in posix_spawn(&childPID, path, &actions, &attributes, args.baseAddress!, env.baseAddress!) } } }
            guard spawned == 0 else { throw MightyError("CLI를 실행하지 못했습니다: \(String(cString: strerror(spawned)))") }
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
                lock.lock(); exitedAt = Date(); lock.unlock()
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
            while true {
                lock.lock(); let ended = exitedAt; lock.unlock()
                if let ended, Date().timeIntervalSince(ended) > 1 { return }
                var descriptor = pollfd(fd: fd, events: Int16(POLLIN | POLLHUP), revents: 0)
                let ready = Darwin.poll(&descriptor, 1, 100)
                if ready < 0 { if errno == EINTR { continue }; return }
                if ready == 0 { continue }
                let count = Darwin.read(fd, &bytes, bytes.count)
                if count > 0 { callback(Data(bytes.prefix(count))) }
                else if count == 0 { return }
                else if errno != EAGAIN && errno != EINTR { return }
            }
        }
    }

    func write(_ data: Data, closeAfter: Bool = false) {
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
    func closeInput() { writer.async { [self] in if stdinFD >= 0 { Darwin.close(stdinFD); stdinFD = -1 } } }
    /// Whether a write would still reach the child. Checked on the writer
    /// queue after any queued close, so a just-exited child reports false.
    var inputIsOpen: Bool {
        writer.sync { [self] in
            lock.lock(); let ended = stopping || exitedAt != nil; lock.unlock()
            return stdinFD >= 0 && !ended
        }
    }
    private func terminateGroup() {
        guard pid > 0, Darwin.kill(-pid, 0) == 0 else { return }
        _ = Darwin.kill(-pid, SIGTERM)
        usleep(150_000)
        _ = Darwin.kill(-pid, SIGKILL)
    }
    func stop() {
        lock.lock(); let shouldStop = !stopping && result == nil; stopping = true; lock.unlock()
        guard shouldStop else { return }
        closeInput()
        _ = Darwin.kill(-pid, SIGTERM)
        DispatchQueue.global(qos: .utility).asyncAfter(deadline: .now() + 0.2) { [self] in
            lock.lock(); let alive = result == nil; lock.unlock()
            if alive { _ = Darwin.kill(-pid, SIGKILL) }
        }
    }
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
            error = MightyError("프로세스 출력 크기 제한을 초과했습니다."); let child = process; lock.unlock(); child?.stop(); return
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
                do { try await Task.sleep(nanoseconds: UInt64(max(0.01, timeout) * 1_000_000_000)); state.cancel(MightyError("프로세스 응답 시간이 초과되었습니다.")) } catch { }
            }
            let code = await process.wait(timeout: timeout + 2)
            deadline.cancel()
            return try state.result(code)
        }, onCancel: { state.cancel(CancellationError()) })
    }
}

private enum ChildEvent: Sendable { case stdout(Data), stderr(Data), exit(Int32) }

/// Pipe callbacks run off-actor. Remember any dropped RPC chunk even if a later
/// burst also evicts the event that first exposed it.
private final class ChildOutputIntegrity: @unchecked Sendable {
    private let lock = NSLock()
    private var lost = false
    func record(_ result: AsyncStream<ChildEvent>.Continuation.YieldResult) {
        if case .dropped = result { lock.lock(); lost = true; lock.unlock() }
    }
    var hasLoss: Bool { lock.lock(); defer { lock.unlock() }; return lost }
}
private final class ManagedProcess {
    let request: StartRunRequest
    let activityId = UUID().uuidString
    var child: NativeChildProcess?
    var bridge: ModBridge?
    var parser: CLIStreamParser?
    var permissions: ClaudePermissionChannel?
    var codexPermissions: CodexApprovalChannel?
    let outputIntegrity = ChildOutputIntegrity()
    var transportFailed = false
    var receivedClaudeResult = false
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
    init(_ request: StartRunRequest) { self.request = request }
}

public actor ProcessRunner {
    private let providerService: ProviderService
    private let pluginDirectory: URL
    private let onEvent: @Sendable (RunEvent) -> Void
    private var runs: [String: ManagedProcess] = [:]
    private var shuttingDown = false
    public init(providerService: ProviderService, pluginDirectory: URL, onEvent: @escaping @Sendable (RunEvent) -> Void) { self.providerService = providerService; self.pluginDirectory = pluginDirectory; self.onEvent = onEvent }

    public func start(request: StartRunRequest, workspace: Workspace, allowPermissionPrompts: Bool = false) async throws {
        try Task.checkCancellation()
        try CoreValidation.validate(request)
        guard !shuttingDown else { throw MightyError("앱이 종료 중입니다.") }
        guard runs[request.sessionId] == nil else { throw MightyError("이 실행 창은 이미 실행 중입니다.") }
        guard runs.count < 16 else { throw MightyError("동시에 실행할 수 있는 창은 16개입니다.") }
        guard workspace.id == request.workspaceId, workspace.remote == nil, StateRepository.absolutePath(workspace.path, remote: false) else { throw MightyError("이 컴퓨터의 승인된 워크스페이스에서 실행해야 합니다.") }
        var directory: ObjCBool = false
        guard FileManager.default.fileExists(atPath: workspace.path, isDirectory: &directory), directory.boolValue else { throw MightyError("워크스페이스 폴더를 찾을 수 없습니다.") }
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
                guard let command = await providerService.command(provider: request.provider, workspacePath: workspace.path, snapshot: snapshot) else { throw MightyError("\(ProviderOptions.label(request.provider)) CLI 실행 파일을 찾을 수 없습니다.") }
                try Task.checkCancellation()
                guard !run.stopping, !shuttingDown, !run.finished else { await cancelPending(run); if !request.attachments.isEmpty { throw CancellationError() }; return }
                if request.provider == "claude", !ProviderService.supportsMods(command.version) { throw MightyError("이 앱의 Mods 연결은 2.1.271 공개 타입을 기준으로 합니다. 현재 \(command.version)에서는 Claude 실행을 지원하지 않습니다.") }
                try CoreValidation.validateCapabilities(request, capabilities: ProviderService.capabilities(provider: request.provider, version: command.version))
                let catalog = await providerService.modelCatalog(provider: request.provider, workspacePath: workspace.path, snapshot: snapshot)
                try Task.checkCancellation()
                guard !run.stopping, !shuttingDown, !run.finished else { await cancelPending(run); if !request.attachments.isEmpty { throw CancellationError() }; return }
                try CoreValidation.validateSelection(request, catalog: catalog, registeredModels: request.registeredModels)
                if request.provider == "claude" {
                    guard FileManager.default.fileExists(atPath: pluginDirectory.appendingPathComponent(".claude-plugin/plugin.json").path) else { throw MightyError("Mighty bridge Mod 파일을 찾을 수 없습니다.") }
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
                if codexApprovals {
                    arguments = try ProviderService.arguments(request, pluginDirectory: pluginDirectory, allowPermissionPrompts: allowPermissionPrompts)
                } else {
                    let prepared = try ProviderInput.prepare(request, pluginDirectory: pluginDirectory, attachments: attachments, allowPermissionPrompts: interactivePermissions)
                    arguments = prepared.arguments; standardInput = prepared.standardInput
                }
                if request.provider == "claude", request.settings.effort != "default" { environment["CLAUDE_CODE_EFFORT_LEVEL"] = request.settings.effort }
                if interactivePermissions {
                    run.permissions = ClaudePermissionChannel(runId: run.activityId, prompt: standardInput,
                        write: { [weak run] data in guard let run, !run.stopping, !run.finished else { return }; run.child?.write(data) },
                        emit: { [weak run, onEvent] permission in guard let run else { return }; onEvent(RunEvent(sessionId: run.request.sessionId, type: "permission", permission: permission)) },
                        activity: { [weak run] permission, state in run?.parser?.permissionActivity(permission, state: state) },
                        warning: { [weak self, weak run] message in guard let self, let run else { return }; self.emitLogSynchronously(run, kind: "system", text: message) },
                        fail: { [weak self, weak run] message in guard let self, let run else { return }; self.emitLogSynchronously(run, kind: "error", text: message); run.child?.stop() })
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
                        // possible while allowing one-shot shutdown afterwards.
                        run.permissions?.cancelAll(); run.permissionInitializationTask?.cancel(); run.child?.closeInput()
                    }, usage: { [weak run, onEvent] usage in
                        guard let run, !run.finished else { return }
                        onEvent(RunEvent(sessionId: run.request.sessionId, type: "usage", usage: usage))
                    }, graph: { [weak run, onEvent] node in
                        // finish() marks the process finished before flush();
                        // final graph snapshots still precede terminal status.
                        guard let run, !run.finalized else { return }
                        onEvent(RunEvent(sessionId: run.request.sessionId, type: "graph", graph: node))
                    }, graphInput: request.input)
                if codexApprovals {
                    run.codexPermissions = CodexApprovalChannel(runId: run.activityId, request: request, workspacePath: workspace.path, attachments: attachments,
                        write: { [weak run] data in guard let run, !run.stopping, !run.finished else { return }; run.child?.write(data) },
                        event: { [weak run] data in guard let run, !run.finished else { return }; run.parser?.push(data) },
                        emit: { [weak run, onEvent] permission in guard let run else { return }; onEvent(RunEvent(sessionId: run.request.sessionId, type: "permission", permission: permission)) },
                        activity: { [weak run] permission, state in run?.parser?.permissionActivity(permission, state: state) },
                        warning: { [weak self, weak run] message in guard let self, let run else { return }; self.emitLogSynchronously(run, kind: "system", text: message) },
                        fail: { [weak self, weak run] message in guard let self, let run else { return }; self.emitLogSynchronously(run, kind: "error", text: message); run.child?.stop() },
                        completed: { [weak run] in run?.permissionInitializationTask?.cancel(); run?.child?.closeInput() })
                }
            }
            let stream = AsyncStream<ChildEvent>.makeStream(bufferingPolicy: .bufferingNewest(256))
            let integrity = run.outputIntegrity
            let child = try NativeChildProcess(executable: executable, arguments: arguments, environment: environment, cwd: URL(fileURLWithPath: workspace.path), stdout: { integrity.record(stream.continuation.yield(.stdout($0))) }, stderr: { integrity.record(stream.continuation.yield(.stderr($0))) }, exited: { integrity.record(stream.continuation.yield(.exit($0))); stream.continuation.finish() })
            run.child = child
            onEvent(RunEvent(sessionId: request.sessionId, type: "status", status: "running"))
            if request.kind == "claude" { Self.deliverActivity(run, activity: AgentActivity(id: run.activityId, provider: request.provider, kind: "turn", state: "running", summary: "\(ProviderOptions.label(request.provider)) 실행 중"), emit: onEvent) }
            emitLog(run, kind: "system", text: request.kind == "shell" ? "명령 실행 · 비대화형 셸" : "\(ProviderOptions.label(request.provider)) CLI 실행")
            run.task = Task { [weak self, weak run] in
                for await event in stream.stream { guard let self, let run else { return }; await self.receive(event, run: run) }
            }
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
              let permissions = run.permissions, permissions.initialized, !permissions.failed, !run.receivedClaudeResult,
              let child = run.child, child.inputIsOpen, let data = try? ProviderInput.claudeUserMessage(text) else { return false }
        child.write(data)
        run.parser?.steer(id: UUID().uuidString, text: text)
        return true
    }

    /// Answers exactly one pending request in exactly one local run. A stale
    /// card from a previous execution of the same pane can never grant access.
    public func respondToPermission(sessionId: String, runId: String, requestId: String, allow: Bool) async throws {
        guard !shuttingDown, let run = runs[sessionId], run.activityId == runId,
              !run.stopping, !run.finished, run.codexPermissions == nil || !run.outputIntegrity.hasLoss else {
            throw MightyError("승인 요청의 실행이 이미 종료되었거나 변경되었습니다.")
        }
        if let permissions = run.permissions { try permissions.respond(requestId: requestId, allow: allow) }
        else if let permissions = run.codexPermissions { try permissions.respond(requestId: requestId, allow: allow) }
        else { throw MightyError("이 실행은 대화형 승인을 지원하지 않습니다.") }
    }
    public func answerUserQuestions(sessionId: String, runId: String, requestId: String, answers: [String: UserQuestionAnswer]) async throws {
        guard !shuttingDown, let run = runs[sessionId], run.activityId == runId,
              !run.stopping, !run.finished, let permissions = run.permissions else {
            throw MightyError("선택 요청의 실행이 이미 종료되었거나 변경되었습니다.")
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
            if run.outputBytes > 2 * 1024 * 1024 { run.truncated = true; emit(RunEvent(sessionId: run.request.sessionId, type: "log", entry: LogEntry(kind: "system", text: "출력이 2 MB를 넘어 이후 표시를 생략합니다."))); return }
        }
        let clean = text.replacingOccurrences(of: "\\x1b(?:\\[[0-?]*[ -/]*[@-~]|\\][^\\x07]*(?:\\x07|\\x1b\\\\))", with: "", options: .regularExpression).unicodeScalars.filter { $0.value == 9 || $0.value == 10 || $0.value == 13 || $0.value >= 32 && $0.value != 127 }
        let value = String(String.UnicodeScalarView(clean))
        if kind == "assistant" {
            // Preserve one logical Markdown message (including code fences and
            // tables). Arbitrary 16K chunks are not separate assistant turns.
            let bounded = ActivitySupport.prefixUTF8(value, maximumBytes: 131_072)
            emit(RunEvent(sessionId: run.request.sessionId, type: "log", entry: LogEntry(kind: kind, text: bounded, provider: run.request.provider)))
            if bounded.utf8.count < value.utf8.count { emit(RunEvent(sessionId: run.request.sessionId, type: "log", entry: LogEntry(kind: "system", text: "응답 한 메시지가 128 KiB를 넘어 뒷부분을 생략했습니다.", provider: run.request.provider))) }
            return
        }
        var offset = value.startIndex
        while offset < value.endIndex {
            let end = value.index(offset, offsetBy: 16_384, limitedBy: value.endIndex) ?? value.endIndex
            emit(RunEvent(sessionId: run.request.sessionId, type: "log", entry: LogEntry(kind: kind, text: String(value[offset..<end]), provider: run.request.kind == "claude" ? run.request.provider : nil)))
            offset = end
        }
    }
    private func receive(_ event: ChildEvent, run: ManagedProcess) async {
        guard !run.finished else { return }
        if run.codexPermissions != nil, run.outputIntegrity.hasLoss, !run.transportFailed {
            run.transportFailed = true
            emitLog(run, kind: "error", text: "Codex 출력이 처리 한도를 초과해 승인 연결을 중단했습니다. 실행을 다시 시작하세요.")
            run.codexPermissions?.cancelAll(); run.child?.stop()
        }
        if run.transportFailed {
            if case .exit(let code) = event { await finish(run, code: code) }
            return
        }
        switch event {
        case .stdout(let data):
            if let permissions = run.codexPermissions { permissions.receive(data) }
            else if let parser = run.parser { parser.push(data) }
            else { emitLog(run, kind: "output", text: run.outputDecoder.push(data)) }
        case .stderr(let data): emitLog(run, kind: "output", text: run.errorDecoder.push(data))
        case .exit(let code): await finish(run, code: code)
        }
    }
    private func receiveMod(_ metadata: ModMetadata, run: ManagedProcess) {
        guard !run.finished, !run.stopping, !shuttingDown else { return }
        onEvent(RunEvent(sessionId: run.request.sessionId, type: "resume", resumeId: metadata.claudeSessionId))
        if metadata.event == "session.start" { emitLog(run, kind: "system", text: "Claude Mods 연결됨 · Mighty bridge") }
        run.parser?.receiveMod(metadata)
    }
    private func cancelPending(_ run: ManagedProcess) async {
        await run.bridge?.stop()
        if run.finished { await waitForFinalization(run); return }
        await finish(run, code: -1)
    }
    private func finish(_ run: ManagedProcess, code: Int32) async {
        guard !run.finished else { return }
        run.codexPermissions?.flush()
        run.finished = true; run.parser?.flush(); emitLog(run, kind: "output", text: run.outputDecoder.flush()); emitLog(run, kind: "output", text: run.errorDecoder.flush())
        run.permissions?.cancelAll(); run.codexPermissions?.cancelAll(); run.permissionInitializationTask?.cancel()
        let incompletePermissionRun = run.permissions != nil && (run.permissions?.initialized != true || !run.receivedClaudeResult)
        if incompletePermissionRun, !run.stopping, !shuttingDown, run.permissions?.failed != true {
            emitLog(run, kind: "error", text: "Claude가 승인 채널 초기화 또는 응답 결과를 전달하기 전에 종료되었습니다.")
        }
        let incompleteCodexRun = run.codexPermissions != nil && (run.codexPermissions?.initialized != true || run.codexPermissions?.turnCompleted != true)
        if incompleteCodexRun, !run.stopping, !shuttingDown, run.codexPermissions?.failed != true {
            emitLog(run, kind: "error", text: "Codex가 승인 채널 초기화 또는 응답 결과를 전달하기 전에 종료되었습니다.")
        }
        if let bridge = run.bridge, await bridge.receivedCount == 0, !run.stopping { emitLog(run, kind: "system", text: "Mods 이벤트를 받지 못했습니다. CLI 출력만 표시하며 관리자 정책과 function hooks 설정을 확인해 주세요.") }
        await run.bridge?.stop()
        let status = run.stopping || shuttingDown ? "stopped" : code == 0 && !run.transportFailed && run.parser?.failed != true && run.permissions?.failed != true && run.codexPermissions?.failed != true && !incompletePermissionRun && !incompleteCodexRun ? "completed" : "error"
        run.parser?.finishActivities(stopped: status == "stopped")
        run.parser?.finishGraph(state: status)
        run.attachments?.cleanup(); run.attachments = nil
        if run.request.kind == "claude" {
            let summary = status == "completed" ? "응답 완료" : status == "stopped" ? "실행 중지" : "실행 오류"
            Self.deliverActivity(run, activity: AgentActivity(id: run.activityId, provider: run.request.provider, kind: "turn", state: status, summary: summary), emit: onEvent)
        }
        onEvent(RunEvent(sessionId: run.request.sessionId, type: "status", status: status))
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
        if let child = run.child { child.stop(); let code = await child.wait(timeout: 3); if code == -1 { run.task?.cancel() } else { await run.task?.value }; if !run.finished { await finish(run, code: -1) } else { await waitForFinalization(run) } }
        else { await cancelPending(run) }
    }
    public func shutdown() async { shuttingDown = true; for id in Array(runs.keys) { await stop(id: id) } }
}
