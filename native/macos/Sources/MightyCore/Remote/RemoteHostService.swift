import Foundation
import Security

/// "이 컴퓨터 공유": serves the chosen local workspaces to other Mighty Claude
/// apps on this computer's Tailscale addresses only (docs/remote-workspaces.md).
/// Runs go through this Mac's own `ProcessRunner`, so they use its CLIs, sign-ins
/// and accounts; their parsed events wait in a per-run buffer the client reads by
/// cursor. A run the client stops polling for `RemoteProtocol.heartbeatTimeout`
/// is stopped. Sharing never survives the app; stopping it revokes the key.
public actor RemoteHostService {
    private struct Job {
        var events: [RemoteWireEvent] = []
        var cursor = 0
        var bytes = 0
        var lastPoll = Date()
        var done = false
        var stopping = false
        var finishedAt: Date?
        var task: Task<Void, Never>?
    }
    private struct Failure: Error { let status: Int; let message: String; init(_ status: Int, _ message: String) { self.status = status; self.message = message } }

    private let repository: StateRepository
    private let providers: ProviderService
    private let pluginDirectory: URL
    private let appVersion: String
    private let hostName: String
    /// Loopback stands in for Tailscale; only tests set it, in code.
    private let testing: Bool
    private let hostId = UUID().uuidString
    private var state = RemoteHostState()
    private var servers: [HTTPServer] = []
    private var runner: ProcessRunner?
    private var runnerEvents: Task<Void, Never>?
    private var leaseTask: Task<Void, Never>?
    private var generation = UUID()
    private var changing = false
    /// Set by `shutdown()`: no sharing starts again in this app's life.
    private var closed = false
    /// "원격에서 전체 권한·명령 실행 허용": off unless the user turns it on while sharing.
    private var allowsFullAccess = false
    private var jobs: [String: Job] = [:]
    private var rateWindow = Date.distantPast
    private var rateCount = 0
    private var peerRates: [String: (start: Date, count: Int)] = [:]

    /// Seconds a run may go unpolled before it is stopped; tests shorten it.
    private let heartbeat: TimeInterval
    /// Told how many client runs go on this Mac whenever that changes: while
    /// any does, this Mac's CLIs are in use (updates and plugin changes wait).
    private let runsChanged: @Sendable (Int) -> Void
    /// Why this Mac cannot start a client's run now (its CLI or plugins are
    /// being updated, its Claude models reloaded); nil when it can.
    private let admission: @Sendable (StartRunRequest) async -> String?

    public init(repository: StateRepository, providers: ProviderService, pluginDirectory: URL, appVersion: String, hostName: String, allowLoopbackForTests: Bool = false,
                heartbeat: TimeInterval = RemoteProtocol.heartbeatTimeout, admission: @escaping @Sendable (StartRunRequest) async -> String? = { _ in nil },
                runsChanged: @escaping @Sendable (Int) -> Void = { _ in }) {
        self.repository = repository; self.providers = providers; self.pluginDirectory = pluginDirectory
        self.appVersion = appVersion; self.hostName = String(hostName.prefix(120)); self.testing = allowLoopbackForTests; self.heartbeat = heartbeat
        self.admission = admission; self.runsChanged = runsChanged
    }

    private func announceRuns() { runsChanged(jobs.values.filter { !$0.done }.count) }

    public func current() -> RemoteHostState {
        var value = state
        value.activeRuns = jobs.values.filter { !$0.done }.count
        value.allowsFullAccess = allowsFullAccess
        return value
    }

    /// Whether clients may run commands and full-access agents here; takes
    /// effect from the next run they start.
    public func setAllowsFullAccess(_ allowed: Bool) -> RemoteHostState {
        allowsFullAccess = allowed
        return current()
    }

    /// What a client may not run here while full access is off: a command pane,
    /// or an agent in full access (Claude's bypassPermissions, Codex's
    /// danger-full-access, Antigravity's full access). nil when it may.
    static func fullAccessRefusal(_ request: StartRunRequest, allowed: Bool) -> String? {
        guard !allowed else { return nil }
        if request.kind == SessionKind.shell { return L("remote.host.error.commandsOff") }
        if request.settings.permissionMode == "fullAccess" { return L("remote.host.error.fullAccessOff") }
        return nil
    }

    /// Starts sharing `workspaceIds` (registered local workspaces) on every
    /// Tailscale address this computer has, with a new key.
    public func start(workspaceIds: [String], port: Int = RemoteProtocol.defaultPort) async throws -> RemoteHostState {
        guard !changing, !state.enabled, servers.isEmpty else { throw MightyError(L("remote.host.error.busy")) }
        guard !workspaceIds.isEmpty, workspaceIds.count <= RemoteProtocol.maximumSharedWorkspaces, Set(workspaceIds).count == workspaceIds.count,
              workspaceIds.allSatisfy(CoreValidation.identifier) else { throw MightyError(L("remote.host.error.pickWorkspace")) }
        guard (1024...65535).contains(port) || (testing && port == 0) else { throw MightyError(L("remote.connect.error.badPort")) }
        guard !closed else { throw MightyError(L("common.appClosing")) }
        changing = true
        defer { changing = false }
        // Taken before the first wait: a stop meanwhile changes it and ends this start.
        let revision = UUID(); generation = revision
        func checked(_ step: () async throws -> Void) async throws {
            try await step()
            guard revision == generation, !closed else { throw MightyError(L("remote.host.error.startCancelled")) }
        }
        try await checked {
            let snapshot = try await repository.load()
            for id in workspaceIds {
                guard snapshot.workspaces.contains(where: { $0.id == id && $0.remote == nil }) else { throw MightyError(L("remote.host.error.localOnly")) }
                _ = try await repository.resolveLocalWorkspace(id: id)
            }
        }
        let addresses = testing ? ["127.0.0.1"] : TailscaleAddresses.current()
        guard !addresses.isEmpty else { throw MightyError(L("remote.host.error.noTailscale")) }
        var random = [UInt8](repeating: 0, count: 32)
        guard SecRandomCopyBytes(kSecRandomDefault, random.count, &random) == errSecSuccess else { throw MightyError(L("remote.host.error.keyFailed")) }
        let key = Data(random).base64EncodedString().replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_").replacingOccurrences(of: "=", with: "")
        let stream = AsyncStream<RunEvent>.makeStream(bufferingPolicy: .unbounded)
        let sink = stream.continuation
        // No pane MCP server (terminal and web tools), no delegation and no
        // picture cache: those belong to panes on this Mac, not to a client's run.
        let runner = ProcessRunner(providerService: providers, pluginDirectory: pluginDirectory) { sink.yield($0) }
        self.runner = runner
        runnerEvents = Task { for await event in stream.stream { self.receive(event, generation: revision) } }
        let allowLoopback = testing
        var bound = port
        do {
            for address in addresses {
                // A stop (or quit) while an address was being bound ends this start.
                guard revision == generation, !closed else { throw CancellationError() }
                // Refused at accept: anything but a Tailscale peer, and a ninth
                // connection from one peer; pinned to the tunnel that owns the address.
                let server = HTTPServer(address: address, port: UInt16(bound), requestBodyLimit: { head in
                    Self.bodyLimit(head, key: key, allowLoopback: allowLoopback)
                }, acceptPeer: { RemotePeerPolicy.allowed($0, allowLoopback: allowLoopback) }, maximumConnectionsPerPeer: RemoteProtocol.maximumConnectionsPerPeer,
                   requiredInterfaceName: testing ? nil : TailscaleAddresses.interface(owning: address)) { request in await self.serve(request, generation: revision) }
                servers.append(server)
                bound = Int(try await server.start())
            }
            guard revision == generation, !closed else { throw CancellationError() }
        } catch {
            // A stop that came in meanwhile already closed what this start had opened.
            if revision == generation { await stopNow() }
            if error is CancellationError { throw MightyError(L("remote.host.error.startCancelled")) }
            throw MightyError(L("remote.host.error.listenFailed", ["error": error.localizedDescription]))
        }
        state = RemoteHostState()
        state.enabled = true; state.key = key; state.port = bound; state.workspaceIds = workspaceIds
        allowsFullAccess = false
        state.addresses = addresses.map { RemoteAddress(host: $0, port: bound).display }
        leaseTask = Task {
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(1))
                if Task.isCancelled { break }
                await self.expire(generation: revision)
            }
        }
        return current()
    }

    /// Stops sharing: every remote run here stops and the key is gone for good.
    public func stop() async -> RemoteHostState {
        await stopNow()
        return current()
    }

    private func stopNow() async {
        generation = UUID()
        let oldServers = servers, oldRunner = runner, oldEvents = runnerEvents
        servers = []; runner = nil; runnerEvents = nil; state = RemoteHostState(); allowsFullAccess = false
        leaseTask?.cancel(); leaseTask = nil
        for job in jobs.values { job.task?.cancel() }
        jobs.removeAll()
        announceRuns()
        for server in oldServers { await server.stop() }
        await oldRunner?.shutdown()
        oldEvents?.cancel()
    }

    // MARK: Requests

    /// How much body is read, decided from the head before any of it: none
    /// without the key (the head alone gets its 401), the larger attachment
    /// body only for an authenticated run request.
    nonisolated static func bodyLimit(_ head: HTTPRequest, key: String, allowLoopback: Bool) -> Int {
        let authorization = head.headers["authorization"] ?? ""
        guard head.headers["origin"] == nil, RemotePeerPolicy.allowed(head.remoteAddress, allowLoopback: allowLoopback),
              head.headers[RemoteProtocol.header] == "\(RemoteProtocol.version)", authorization.hasPrefix("Bearer "), authorization.count <= 256,
              RemoteWorkspaceValidation.keysMatch(String(authorization.dropFirst(7)), key) else { return 0 }
        return head.method == "POST" && head.target == RemoteProtocol.pathPrefix + "runs" ? AttachmentSupport.maximumRequestBytes : RemoteProtocol.maximumRequestBytes
    }

    /// Every request: sharing on, no browser origin, a Tailscale peer, within
    /// the rate limit, the right key (compared in constant time), the protocol version.
    private func authorize(_ request: HTTPRequest, generation revision: UUID) throws {
        guard state.enabled, revision == generation, let key = state.key else { throw Failure(503, L("remote.host.error.stopped")) }
        guard request.headers["origin"] == nil, RemotePeerPolicy.allowed(request.remoteAddress, allowLoopback: testing) else { throw Failure(403, L("remote.host.error.tailscaleOnly")) }
        // A client polls each of its runs at most ten times a second. Each peer
        // has its own budget; only requests with the key count toward the
        // shared one, so a device without it cannot crowd the real client out.
        let now = Date()
        var peer = peerRates[request.remoteAddress] ?? (now, 0)
        if now.timeIntervalSince(peer.start) >= 1 { peer = (now, 0) }
        peer.count += 1
        if peerRates[request.remoteAddress] == nil, peerRates.count >= 64 { peerRates.removeAll() }
        peerRates[request.remoteAddress] = peer
        guard peer.count <= 300 else { throw Failure(429, L("remote.host.error.tooMany")) }
        let authorization = request.headers["authorization"] ?? ""
        guard authorization.hasPrefix("Bearer "), authorization.count <= 256, RemoteWorkspaceValidation.keysMatch(String(authorization.dropFirst(7)), key) else {
            throw Failure(401, L("remote.host.error.badKey"))
        }
        if now.timeIntervalSince(rateWindow) >= 1 { rateWindow = now; rateCount = 0 }
        rateCount += 1
        guard rateCount <= 600 else { throw Failure(429, L("remote.host.error.tooMany")) }
        guard request.headers[RemoteProtocol.header] == "\(RemoteProtocol.version)" else { throw Failure(426, L("remote.host.error.version")) }
    }

    private func reply<T: Encodable>(_ status: Int, _ value: T) -> HTTPResponse {
        let encoder = JSONEncoder(); encoder.outputFormatting = [.withoutEscapingSlashes]
        return HTTPResponse(status: status, body: (try? encoder.encode(value)) ?? Data("{}".utf8), headers: [RemoteProtocol.header: "\(RemoteProtocol.version)"])
    }
    private func failure(_ status: Int, _ message: String) -> HTTPResponse {
        var response = HTTPResponse.json(status, ["protocol": RemoteProtocol.version, "error": String(message.prefix(1000))])
        response.headers[RemoteProtocol.header] = "\(RemoteProtocol.version)"
        return response
    }

    private func serve(_ request: HTTPRequest, generation revision: UUID) async -> HTTPResponse {
        do {
            try authorize(request, generation: revision)
            guard let url = URLComponents(string: request.target), url.scheme == nil, url.host == nil, url.path.hasPrefix(RemoteProtocol.pathPrefix) else { throw Failure(400, L("remote.host.error.badPath")) }
            let route = url.path.dropFirst(RemoteProtocol.pathPrefix.count).split(separator: "/", omittingEmptySubsequences: false).map(String.init)
            if request.method == "GET", route == ["info"], url.query == nil { return try await info(generation: revision) }
            if request.method == "POST", route == ["runs"], url.query == nil { return try await startRun(request, generation: revision) }
            guard route.count == 3, route[0] == "runs", CoreValidation.identifier(route[1]), jobs[route[1]] != nil else { throw Failure(404, L("remote.host.error.runGone")) }
            let id = route[1]
            if request.method == "GET", route[2] == "events" { return try events(id, query: url.queryItems) }
            guard request.method == "POST", url.query == nil else { throw Failure(404, L("remote.host.error.badPath")) }
            if route[2] == "stop" { await stopJob(id); return reply(200, RemoteWireAnswer(accepted: true)) }
            guard request.headers["content-type"]?.split(separator: ";").first?.trimmingCharacters(in: .whitespaces) == "application/json" else { throw Failure(415, L("remote.host.error.jsonOnly")) }
            guard let runner, jobs[id]?.done == false else { throw Failure(409, L("remote.host.error.runGone")) }
            do {
                switch route[2] {
                case "steer":
                    let body = try JSONDecoder().decode(RemoteWireSteer.self, from: request.body)
                    guard !body.text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, body.text.utf8.count <= 400_000, !body.text.contains("\0") else { throw Failure(400, L("validation.run.badInput")) }
                    return reply(200, RemoteWireAnswer(accepted: await runner.steer(sessionId: id, text: body.text)))
                case "permission":
                    let body = try JSONDecoder().decode(RemoteWirePermission.self, from: request.body)
                    try await runner.respondToPermission(sessionId: id, runId: body.runId, requestId: body.requestId, allow: body.allow)
                case "questions":
                    let body = try JSONDecoder().decode(RemoteWireQuestions.self, from: request.body)
                    try await runner.answerUserQuestions(sessionId: id, runId: body.runId, requestId: body.requestId, answers: body.answers)
                case "plan":
                    let body = try JSONDecoder().decode(RemoteWirePlan.self, from: request.body)
                    guard let decision = body.decision else { throw Failure(400, L("remote.host.error.badRequest")) }
                    try await runner.answerPlan(sessionId: id, runId: body.runId, requestId: body.requestId, decision: decision)
                default: throw Failure(404, L("remote.host.error.badPath"))
                }
            } catch let failure as Failure { throw failure }
            catch is DecodingError { throw Failure(400, L("remote.host.error.badRequest")) }
            catch { throw Failure(409, error.localizedDescription) }
            return reply(200, RemoteWireAnswer(accepted: true))
        } catch let failure as Failure { return self.failure(failure.status, failure.message) }
        catch { return failure(500, error.localizedDescription) }
    }

    private func sharedWorkspaces() async throws -> [Workspace] {
        let snapshot = try await repository.load()
        return snapshot.workspaces.filter { workspace in
            workspace.remote == nil && state.workspaceIds.contains(workspace.id)
                && RemoteWorkspaceValidation.workspace(RemoteSharedWorkspace(id: workspace.id, name: workspace.name, path: workspace.path))
        }
    }

    private func info(generation revision: UUID) async throws -> HTTPResponse {
        let workspaces = try await sharedWorkspaces()
        let runtime = await providers.runtimeInfo(appVersion: appVersion)
        guard revision == generation else { throw Failure(503, L("remote.host.error.stopped")) }
        return reply(200, RemoteWireInfo(hostId: hostId, hostName: hostName.isEmpty ? "Mac" : hostName,
                                         workspaces: workspaces.map { RemoteSharedWorkspace(id: $0.id, name: $0.name, path: $0.path) }, runtime: runtime))
    }

    private func startRun(_ request: HTTPRequest, generation revision: UUID) async throws -> HTTPResponse {
        guard request.headers["content-type"]?.split(separator: ";").first?.trimmingCharacters(in: .whitespaces) == "application/json" else { throw Failure(415, L("remote.host.error.jsonOnly")) }
        let wire: RemoteWireStart
        do { wire = try JSONDecoder().decode(RemoteWireStart.self, from: request.body) } catch { throw Failure(400, L("remote.host.error.badRequest")) }
        var run = wire.request
        run.registeredModels = Array(wire.registeredModels.prefix(64))
        run.permissionModeOverride = wire.permissionModeOverride
        run.todoProgress = wire.todoProgress
        if let phase = wire.phaseModels {
            guard RemoteWorkspaceValidation.phaseModels(phase) else { throw Failure(400, L("remote.host.error.badRequest")) }
            run.phaseModels = phase.toPhaseModelConfig(omcAgents: nil, ouroborosKeys: nil)
        }
        do { try CoreValidation.validate(run) } catch { throw Failure(400, error.localizedDescription) }
        guard run.registeredModels.allSatisfy({ CoreValidation.model($0.name) }) else { throw Failure(400, L("remote.host.error.badRequest")) }
        guard state.workspaceIds.contains(run.workspaceId), let workspace = try await sharedWorkspaces().first(where: { $0.id == run.workspaceId }) else { throw Failure(403, L("remote.host.error.notShared")) }
        // Only a shared workspace's own folder is ever the working folder.
        let folder: Workspace
        do { folder = try await repository.resolveLocalWorkspace(id: workspace.id) } catch { throw Failure(403, error.localizedDescription) }
        guard folder.path == workspace.path else { throw Failure(403, L("remote.host.error.notShared")) }
        if let refusal = Self.fullAccessRefusal(run, allowed: allowsFullAccess) { throw Failure(403, refusal) }
        // This Mac's own CLI update, plugin change or model reload holds client runs too.
        if let reason = await admission(run) { throw Failure(503, reason) }
        if run.kind == SessionKind.claude {
            // What this computer cannot run is refused here, before anything starts.
            let runtime = await providers.providerRuntime(provider: run.provider, workspacePath: folder.path)
            guard runtime.available else { throw Failure(400, L("remote.host.error.cliMissing", ["name": ProviderOptions.label(run.provider)])) }
            do {
                try CoreValidation.validateCapabilities(run, capabilities: runtime.capabilities)
                try CoreValidation.validateSelection(run, catalog: runtime.modelCatalog, registeredModels: run.registeredModels)
            } catch { throw Failure(400, error.localizedDescription) }
        }
        guard revision == generation, let runner else { throw Failure(503, L("remote.host.error.stopped")) }
        guard jobs.values.filter({ !$0.done }).count < RemoteProtocol.maximumRuns else { throw Failure(429, L("remote.host.error.runLimit", ["count": "\(RemoteProtocol.maximumRuns)"])) }
        prune()
        let id = UUID().uuidString
        jobs[id] = Job()
        announceRuns()
        run.sessionId = id
        let permissions = run.kind == SessionKind.claude && (run.provider == "claude" || (run.provider == "codex" && run.settings.permissionMode == "onRequest"))
        let launched = run
        jobs[id]?.task = Task { await self.launch(launched, workspace: folder, runner: runner, permissions: permissions, generation: revision) }
        return reply(202, RemoteWireJob(jobId: id))
    }

    private func events(_ id: String, query: [URLQueryItem]?) throws -> HTTPResponse {
        guard let job = jobs[id], let query, query.count == 1, query[0].name == "cursor", let raw = query[0].value,
              raw.range(of: "^[0-9]{1,12}$", options: .regularExpression) != nil, let cursor = Int(raw), cursor <= job.cursor else { throw Failure(400, L("remote.host.error.badCursor")) }
        jobs[id]?.lastPoll = Date()
        var selected: [RemoteWireEvent] = []; var bytes = 0
        for item in job.events where item.cursor > cursor {
            let size = (try? JSONEncoder().encode(item).count) ?? 0
            guard selected.count < RemoteProtocol.maximumPollEvents, bytes + size <= RemoteProtocol.maximumPollBytes else { break }
            selected.append(item); bytes += size
        }
        // A cursor skipped between the request's and what follows: the buffer
        // dropped it (over its limits, or one event too large to send).
        var expected = cursor + 1, gap = false
        for item in selected { if item.cursor != expected { gap = true }; expected = item.cursor + 1 }
        // Nothing buffered after the cursor although the run went on: all of it
        // was dropped (the first buffered event after a cursor always fits a poll).
        if selected.isEmpty, job.cursor > cursor { gap = true }
        let last = selected.last?.cursor ?? (gap ? job.cursor : cursor)
        return reply(200, RemoteWirePoll(cursor: last, lastCursor: job.cursor, gap: gap, done: job.done && last >= job.cursor, events: selected))
    }

    // MARK: Runs

    private func launch(_ request: StartRunRequest, workspace: Workspace, runner: ProcessRunner, permissions: Bool, generation revision: UUID) async {
        guard !Task.isCancelled, revision == generation, jobs[request.sessionId]?.done == false, jobs[request.sessionId]?.stopping == false else { return }
        do { try await runner.start(request: request, workspace: workspace, allowPermissionPrompts: permissions) }
        catch {
            guard revision == generation, jobs[request.sessionId]?.done == false else { return }
            receive(RunEvent(sessionId: request.sessionId, type: "log", entry: LogEntry(kind: "error", text: error.localizedDescription)), generation: revision)
            receive(RunEvent(sessionId: request.sessionId, type: "status", status: jobs[request.sessionId]?.stopping == true ? "stopped" : "error"), generation: revision)
        }
    }

    private func receive(_ event: RunEvent, generation revision: UUID) {
        guard revision == generation, var job = jobs[event.sessionId], !job.done else { return }
        job.cursor += 1
        let wire = RemoteWireEvent(cursor: job.cursor, event: event)
        let size = (try? JSONEncoder().encode(wire).count) ?? Int.max
        // One event too large for any poll is never sent; its cursor shows as a gap.
        if size <= RemoteProtocol.maximumPollBytes - 4096 {
            job.events.append(wire); job.bytes += size
            while job.events.count > RemoteProtocol.maximumBufferedEvents || job.bytes > RemoteProtocol.maximumBufferedBytes {
                job.bytes -= (try? JSONEncoder().encode(job.events.removeFirst()).count) ?? 0
            }
        }
        let ended = event.type == "status" && ["completed", "error", "stopped"].contains(event.status ?? "")
        if ended { job.done = true; job.finishedAt = Date() }
        jobs[event.sessionId] = job
        if ended { announceRuns() }
    }

    private func stopJob(_ id: String) async {
        guard jobs[id]?.done == false, jobs[id]?.stopping == false else { return }
        let revision = generation
        jobs[id]?.stopping = true; jobs[id]?.task?.cancel()
        runner?.signalStop(id: id)
        await runner?.stop(id: id)
        if revision == generation, jobs[id]?.done == false { receive(RunEvent(sessionId: id, type: "status", status: "stopped"), generation: revision) }
    }

    /// The heartbeat: a run whose client stopped polling is stopped.
    private func expire(generation revision: UUID) async {
        guard revision == generation else { return }
        let orphaned = jobs.filter { !$0.value.done && !$0.value.stopping && Date().timeIntervalSince($0.value.lastPoll) >= heartbeat }.map(\.key)
        for id in orphaned { await stopJob(id) }
        prune()
    }

    private func prune() {
        for (id, job) in jobs where job.done && Date().timeIntervalSince(job.finishedAt ?? Date()) > 120 { jobs.removeValue(forKey: id) }
        while jobs.count >= 2 * RemoteProtocol.maximumRuns, let oldest = jobs.filter({ $0.value.done }).min(by: { ($0.value.finishedAt ?? .distantPast) < ($1.value.finishedAt ?? .distantPast) }) {
            jobs.removeValue(forKey: oldest.key)
        }
    }

    public func shutdown() async { closed = true; await stopNow() }
}
