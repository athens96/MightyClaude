import Foundation

/// "다른 컴퓨터 연결": saved connections to other computers' Mighty Claude, the
/// remote workspaces opened through them, and the runs of their panes
/// (docs/remote-workspaces.md). A run's events are the host runner's own,
/// parsed there from the CLI's stream by the same parsers a local run uses, and
/// are applied here as the pane's, so the Default view and the Mighty views draw
/// a remote run as they draw a local one. Polling a run is also its heartbeat.
public actor RemoteClientService {
    private struct Connection {
        var info: RemoteConnectionInfo
        var key: String?
        var target: RemoteTarget?
        /// Saved with a key in the Keychain that was not read yet (read on first use).
        var keyPending = false
        var revision = UUID()
    }
    private struct Run {
        var revision = UUID()
        var connectionId: String
        var target: RemoteTarget
        var key: String
        var jobId: String?
        var cursor = 0
        var stopping = false
        var task: Task<Void, Never>?
    }
    private struct Saved: Codable { var id: String; var name: String; var address: String }
    private struct SavedFile: Codable { var version = 2; var connections: [Saved] }

    private let repository: StateRepository
    private let dataDirectory: URL
    private let onEvent: @Sendable (RunEvent) -> Void
    /// Loopback stands in for Tailscale and keys stay in memory; only tests set it, in code.
    private let testing: Bool
    private var connections: [String: Connection] = [:]
    private var order: [String] = []
    private var runs: [String: Run] = [:]
    private var loaded = false
    private var closing = false
    private var connecting = Set<String>()

    /// Whether the route to a host's IP still leaves from this Mac's Tailscale
    /// tunnel. Asked before every request; tests replace it.
    private let routeCheck: @Sendable (String) -> Bool

    public init(repository: StateRepository, dataDirectory: URL, allowLoopbackForTests: Bool = false, routeCheck: (@Sendable (String) -> Bool)? = nil,
                onEvent: @escaping @Sendable (RunEvent) -> Void) {
        self.repository = repository; self.dataDirectory = dataDirectory; self.testing = allowLoopbackForTests; self.onEvent = onEvent
        self.routeCheck = routeCheck ?? (allowLoopbackForTests ? { _ in true } : Self.routedThroughTailscale)
    }

    /// The route to `ip` leaves from one of this Mac's Tailscale addresses. A
    /// carrier-grade NAT network (or another VPN) can route 100.64/10 elsewhere,
    /// outside WireGuard, and routes change while the app runs.
    static func routedThroughTailscale(_ ip: String) -> Bool {
        guard let source = TailscaleAddresses.sourceAddress(toward: ip) else { return false }
        return TailscaleAddresses.current().contains(source)
    }

    /// Before each request to the host: the route still goes through the
    /// tunnel, or the connection is lost (never retried as a passing failure).
    private func ensureRoute(_ connectionId: String, _ target: RemoteTarget) async throws {
        guard !routeCheck(target.ip) else { return }
        await fail(connectionId, message: L("remote.connect.error.notViaTailscale"))
        throw MightyError(L("remote.connect.error.notViaTailscale"))
    }

    public func list() -> [RemoteConnectionInfo] {
        loadSaved()
        return order.compactMap { connections[$0]?.info }
    }

    public func connection(_ id: String) -> RemoteConnectionInfo? { loadSaved(); return connections[id]?.info }

    /// Connects with a new address and key, or replaces the key of the saved
    /// connection at the same address (a host that restarted sharing has a new one).
    @discardableResult
    public func connect(name: String, address: String, key: String) async throws -> RemoteConnectionInfo {
        loadSaved()
        guard !closing else { throw MightyError(L("common.appClosing")) }
        let label = name.trimmingCharacters(in: .whitespacesAndNewlines)
        let trimmedKey = key.trimmingCharacters(in: .whitespacesAndNewlines)
        guard label.count <= 120, RemoteWorkspaceValidation.key(trimmedKey) else { throw MightyError(L("remote.connect.error.badKey")) }
        let parsed = try RemoteAddress.parse(address)
        let existing = connections.values.first { $0.info.address == parsed.display }
        guard existing != nil || connections.count < RemoteProtocol.maximumConnections else { throw MightyError(L("remote.connect.error.tooMany", ["count": "\(RemoteProtocol.maximumConnections)"])) }
        guard connecting.insert(parsed.display).inserted else { throw MightyError(L("remote.connect.error.busy")) }
        defer { connecting.remove(parsed.display) }
        let (target, info) = try await reach(parsed, key: trimmedKey)
        guard !closing else { throw MightyError(L("common.appClosing")) }
        let id = existing?.info.id ?? UUID().uuidString
        if let existing { await dropRuns(of: existing.info.id, message: nil) }
        let saved = testing || RemoteKeychain.save(trimmedKey, id: id)
        var view = RemoteConnectionInfo(id: id, name: label.isEmpty ? info.hostName : label, address: parsed.display, status: .connected, hostName: info.hostName,
                                        workspaces: info.workspaces, runtime: info.runtime,
                                        detail: saved ? L("remote.connect.detail.connectedSaved") : L("remote.connect.detail.connectedMemory"))
        if let existing, label.isEmpty { view.name = existing.info.name }
        connections[id] = Connection(info: view, key: trimmedKey, target: target)
        if !order.contains(id) { order.append(id) }
        writeSaved()
        return view
    }

    /// Reconnects a saved connection; its key is read from the Keychain here
    /// the first time, never at launch.
    @discardableResult
    public func refresh(id: String) async throws -> RemoteConnectionInfo {
        loadSaved()
        guard !closing else { throw MightyError(L("common.appClosing")) }
        guard var current = connections[id] else { throw MightyError(L("remote.connect.error.unknown")) }
        if current.key == nil, current.keyPending {
            current.key = RemoteKeychain.load(id); current.keyPending = false
            connections[id]?.key = current.key; connections[id]?.keyPending = false
        }
        guard let key = current.key else {
            connections[id]?.info.status = .disconnected; connections[id]?.info.detail = L("remote.connect.error.keyMissing")
            throw MightyError(L("remote.connect.error.keyMissing"))
        }
        guard connecting.insert(current.info.address).inserted else { throw MightyError(L("remote.connect.error.busy")) }
        defer { connecting.remove(current.info.address) }
        do {
            let (target, info) = try await reach(try RemoteAddress.parse(current.info.address), key: key)
            guard !closing, connections[id]?.revision == current.revision else { throw MightyError(L("remote.connect.error.changed")) }
            connections[id]?.target = target
            connections[id]?.info.status = .connected; connections[id]?.info.hostName = info.hostName
            connections[id]?.info.workspaces = info.workspaces; connections[id]?.info.runtime = info.runtime
            connections[id]?.info.detail = L("remote.connect.detail.connected")
            return connections[id]!.info
        } catch {
            if connections[id]?.revision == current.revision { await fail(id, message: error.localizedDescription) }
            throw error
        }
    }

    /// Connected now, reconnecting first when it is not: a remote pane's send does this.
    public func ensureConnected(id: String) async throws -> RemoteConnectionInfo {
        loadSaved()
        // Two panes sending at once after launch share one connect.
        let deadline = Date().addingTimeInterval(RemoteProtocol.requestTimeout + 5)
        while let address = connections[id]?.info.address, connecting.contains(address), Date() < deadline {
            try await Task.sleep(for: .milliseconds(50))
        }
        if let info = connections[id]?.info, info.status == .connected, let target = connections[id]?.target {
            try await ensureRoute(id, target)
            return info
        }
        return try await refresh(id: id)
    }

    /// Stops this connection's runs and keeps it saved; a refresh connects again.
    public func disconnect(id: String) async {
        await dropRuns(of: id, message: nil)
        connections[id]?.revision = UUID(); connections[id]?.target = nil
        connections[id]?.info.status = .disconnected; connections[id]?.info.detail = L("remote.connect.detail.disconnected")
    }

    /// Forgets the connection and its key. Its workspaces stay listed until removed.
    public func remove(id: String) async {
        await disconnect(id: id)
        connections.removeValue(forKey: id); order.removeAll { $0 == id }
        if !testing { RemoteKeychain.delete(id) }
        writeSaved()
    }

    /// Lists the host's shared workspace `hostWorkspaceId` in this app as a remote workspace.
    public func openWorkspace(connectionId: String, hostWorkspaceId: String) async throws -> Workspace {
        guard let connection = connections[connectionId], connection.info.status == .connected,
              let shared = connection.info.workspaces.first(where: { $0.id == hostWorkspaceId }), RemoteWorkspaceValidation.workspace(shared) else {
            throw MightyError(L("remote.workspace.error.notShared"))
        }
        return try await repository.approveRemoteWorkspace(connectionId: connectionId, hostWorkspaceId: shared.id, name: shared.name, path: shared.path,
                                                           hostName: connection.info.hostName ?? connection.info.name)
    }

    private func reach(_ address: RemoteAddress, key: String) async throws -> (RemoteTarget, RemoteWireInfo) {
        let local = testing ? [] : TailscaleAddresses.current()
        if !testing, local.isEmpty { throw MightyError(L("remote.connect.error.localTailscaleOff")) }
        let target = try await RemoteTransport.resolve(address, allowLoopback: testing)
        // The peer must be reached through this Mac's own tunnel: a carrier-grade
        // NAT network can route a 100.64/10 address over Wi-Fi, outside WireGuard.
        if !routeCheck(target.ip) {
            throw MightyError(L("remote.connect.error.notViaTailscale"))
        }
        let data = try await RemoteTransport.request(target, key: key, method: "GET", path: RemoteProtocol.pathPrefix + "info")
        let info: RemoteWireInfo
        do { info = try JSONDecoder().decode(RemoteWireInfo.self, from: data) } catch { throw MightyError(L("remote.connect.error.notMighty")) }
        try RemoteWorkspaceValidation.info(info)
        return (target, info)
    }

    // MARK: Runs

    public func isRunning(_ id: String) -> Bool { runs[id] != nil }

    /// Sends one request of a remote workspace's pane to the host and follows its output.
    public func start(request: StartRunRequest, workspace: Workspace, phaseModels: PhaseModelHardcodedConfig?) async throws {
        try Task.checkCancellation()
        guard !closing else { throw MightyError(L("common.appClosing")) }
        try CoreValidation.validate(request)
        guard runs[request.sessionId] == nil else { throw MightyError(L("run.error.paneAlreadyRunning")) }
        guard runs.count < RemoteProtocol.maximumRuns else { throw MightyError(L("run.error.concurrentLimit")) }
        guard workspace.id == request.workspaceId, let reference = workspace.remote, let connection = connections[reference.connectionId],
              connection.info.status == .connected, let target = connection.target, let key = connection.key,
              connection.info.workspaces.contains(where: { $0.id == reference.workspaceId }) else { throw MightyError(L("remote.run.error.notConnected")) }
        if request.kind == SessionKind.claude {
            guard let provider = connection.info.runtime?.providers?.first(where: { $0.id == request.provider }), provider.available else {
                throw MightyError(L("remote.host.error.cliMissing", ["name": ProviderOptions.label(request.provider)]))
            }
            try CoreValidation.validateCapabilities(request, capabilities: provider.capabilities)
        }
        let run = Run(connectionId: reference.connectionId, target: target, key: key)
        runs[request.sessionId] = run
        var outgoing = request; outgoing.workspaceId = reference.workspaceId
        let wire = RemoteWireStart(request: outgoing, permissionModeOverride: request.permissionModeOverride, registeredModels: request.registeredModels,
                                   todoProgress: request.todoProgress, phaseModels: request.kind == SessionKind.claude ? phaseModels : nil,
                                   antigravityGrant: request.provider == "gemini" ? request.antigravityGrant : nil)
        do {
            let encoder = JSONEncoder(); encoder.outputFormatting = [.withoutEscapingSlashes]
            let body = try encoder.encode(wire)
            guard body.count <= AttachmentSupport.maximumRequestBytes, request.attachments.isEmpty ? body.count <= RemoteProtocol.maximumRequestBytes : true else {
                throw MightyError(L("remote.run.error.tooLarge"))
            }
            try await ensureRoute(reference.connectionId, target)
            let data = try await RemoteTransport.request(target, key: key, method: "POST", path: RemoteProtocol.pathPrefix + "runs", body: body,
                                                         timeout: request.attachments.isEmpty ? 30 : RemoteProtocol.uploadTimeout)
            let accepted = try JSONDecoder().decode(RemoteWireJob.self, from: data)
            guard accepted.protocol == RemoteProtocol.version, CoreValidation.identifier(accepted.jobId) else { throw MightyError(L("remote.connect.error.notMighty")) }
            guard !closing, !Task.isCancelled, runs[request.sessionId]?.revision == run.revision, runs[request.sessionId]?.stopping == false else {
                var cancelled = run; cancelled.jobId = accepted.jobId
                await stopOnHost(cancelled)
                if runs[request.sessionId]?.revision == run.revision { runs.removeValue(forKey: request.sessionId) }
                throw CancellationError()
            }
            runs[request.sessionId]?.jobId = accepted.jobId
            runs[request.sessionId]?.task = Task { await self.follow(request.sessionId, revision: run.revision) }
        } catch {
            if runs[request.sessionId]?.revision == run.revision { runs.removeValue(forKey: request.sessionId) }
            // The host no longer takes this key (it restarted sharing): the
            // connection is not connected any more, whatever it said before.
            if let failure = error as? RemoteHTTPFailure, failure.status == 401 { await fail(reference.connectionId, message: failure.message) }
            throw error
        }
    }

    /// A failed poll worth asking again for a while: the host was busy (429,
    /// 503) or the network stumbled. Anything else ends the run at once.
    static func transient(_ error: Error) -> Bool {
        if let failure = error as? RemoteHTTPFailure { return failure.transient }
        guard let url = error as? URLError else { return false }
        return [.timedOut, .networkConnectionLost, .notConnectedToInternet, .cannotConnectToHost, .cannotFindHost, .dnsLookupFailed].contains(url.code)
    }

    private func follow(_ id: String, revision: UUID) async {
        var quiet = 0
        var failingSince: Date?
        while !Task.isCancelled, let run = runs[id], !run.stopping, run.revision == revision, let jobId = run.jobId {
            do {
                let data: Data
                do {
                    guard routeCheck(run.target.ip) else {
                        await fail(run.connectionId, message: L("remote.connect.error.notViaTailscale")); return
                    }
                    data = try await RemoteTransport.request(run.target, key: run.key, method: "GET", path: RemoteProtocol.pathPrefix + "runs/\(jobId)/events?cursor=\(run.cursor)")
                    failingSince = nil
                } catch where !Task.isCancelled && Self.transient(error) && Date().timeIntervalSince(failingSince ?? Date()) < RemoteProtocol.pollRetryWindow {
                    // Asked again for up to ten seconds, well inside the host's heartbeat.
                    failingSince = failingSince ?? Date()
                    try await Task.sleep(for: .seconds(1))
                    continue
                }
                guard !Task.isCancelled, runs[id]?.revision == revision else { return }
                let poll = try JSONDecoder().decode(RemoteWirePoll.self, from: data)
                guard poll.protocol == RemoteProtocol.version, poll.cursor >= run.cursor, poll.lastCursor >= poll.cursor, poll.events.count <= RemoteProtocol.maximumPollEvents else {
                    throw MightyError(L("remote.run.error.badOutput"))
                }
                if poll.gap { onEvent(RunEvent(sessionId: id, type: "log", entry: LogEntry(kind: "system", text: L("remote.run.notice.gap")))) }
                var cursor = run.cursor, terminal = false, skipped = false
                for item in poll.events {
                    guard item.cursor > cursor, item.cursor <= poll.cursor else { throw MightyError(L("remote.run.error.badOutput")) }
                    cursor = item.cursor
                    // One event the client cannot use is left out, and said so; the run goes on.
                    guard RemoteWorkspaceValidation.event(item.event, sessionId: jobId) else { skipped = true; continue }
                    if let event = Self.local(item.event, pane: id) {
                        onEvent(event)
                        if event.type == "status", ["completed", "error", "stopped"].contains(event.status ?? "") { terminal = true }
                    }
                }
                if skipped { onEvent(RunEvent(sessionId: id, type: "log", entry: LogEntry(kind: "system", text: L("remote.run.notice.skipped")))) }
                cursor = poll.cursor
                runs[id]?.cursor = cursor
                if poll.done {
                    runs.removeValue(forKey: id)
                    if !terminal { onEvent(RunEvent(sessionId: id, type: "status", status: "error")) }
                    return
                }
                // More waits on the host: read it soon (at most ten polls a second
                // per run). Otherwise wait a little, longer while nothing comes.
                if poll.lastCursor > cursor { quiet = 0; try await Task.sleep(for: .milliseconds(100)); continue }
                quiet = poll.events.isEmpty ? min(quiet + 1, 4) : 0
                try await Task.sleep(for: .milliseconds(250 + 150 * quiet))
            } catch {
                if !Task.isCancelled, runs[id]?.revision == revision { await fail(run.connectionId, message: error.localizedDescription) }
                return
            }
        }
    }

    /// A host event as the client pane's own: its id, and only what the client can draw.
    static func local(_ event: RunEvent, pane: String) -> RunEvent? {
        var value = event
        value.sessionId = pane
        if value.type == "usage" { guard let usage = value.usage.flatMap(SessionUsageSupport.normalized) else { return nil }; value.usage = usage }
        if value.type == "graph" { guard let graph = value.graph.flatMap({ ExecutionGraphSupport.normalized($0) }) else { return nil }; value.graph = graph }
        // A picture's cache key names a file on the host.
        value.entry?.images = nil
        return value
    }

    public func stop(id: String) async {
        guard let run = runs[id], !run.stopping else { return }
        runs[id]?.stopping = true
        run.task?.cancel()
        await stopOnHost(run)
        guard runs[id]?.revision == run.revision else { return }
        runs.removeValue(forKey: id)
        onEvent(RunEvent(sessionId: id, type: "status", status: "stopped"))
    }

    private func stopOnHost(_ run: Run) async {
        guard let job = run.jobId, routeCheck(run.target.ip) else { return }
        _ = try? await RemoteTransport.request(run.target, key: run.key, method: "POST", path: RemoteProtocol.pathPrefix + "runs/\(job)/stop", body: Data("{}".utf8), timeout: 5)
    }

    private func send<T: Encodable>(_ id: String, _ action: String, _ body: T) async throws -> Bool {
        guard let run = runs[id], !run.stopping, let job = run.jobId else { throw MightyError(L("run.error.permissionRunGone")) }
        try await ensureRoute(run.connectionId, run.target)
        let data = try await RemoteTransport.request(run.target, key: run.key, method: "POST", path: RemoteProtocol.pathPrefix + "runs/\(job)/\(action)", body: try JSONEncoder().encode(body))
        return (try? JSONDecoder().decode(RemoteWireAnswer.self, from: data))?.accepted == true
    }

    public func respondToPermission(sessionId: String, runId: String, requestId: String, allow: Bool) async throws {
        _ = try await send(sessionId, "permission", RemoteWirePermission(runId: runId, requestId: requestId, allow: allow))
    }
    public func answerUserQuestions(sessionId: String, runId: String, requestId: String, answers: [String: UserQuestionAnswer]) async throws {
        _ = try await send(sessionId, "questions", RemoteWireQuestions(runId: runId, requestId: requestId, answers: answers))
    }
    public func answerPlan(sessionId: String, runId: String, requestId: String, decision: PlanDecision) async throws {
        _ = try await send(sessionId, "plan", RemoteWirePlan(runId: runId, requestId: requestId, decision: decision))
    }

    /// The network or the host failed: the connection shows the error and its runs end in error.
    private func fail(_ id: String, message: String) async {
        connections[id]?.revision = UUID(); connections[id]?.target = nil
        connections[id]?.info.status = .disconnected; connections[id]?.info.detail = String(message.prefix(300))
        await dropRuns(of: id, message: message)
    }

    private func dropRuns(of connectionId: String, message: String?) async {
        let active = runs.filter { $0.value.connectionId == connectionId }
        for (pane, run) in active {
            run.task?.cancel(); runs.removeValue(forKey: pane)
            if let message {
                onEvent(RunEvent(sessionId: pane, type: "log", entry: LogEntry(kind: "error", text: L("remote.run.error.connectionLost", ["detail": String(message.prefix(300))]))))
                onEvent(RunEvent(sessionId: pane, type: "status", status: "error"))
            } else { onEvent(RunEvent(sessionId: pane, type: "status", status: "stopped")) }
        }
        // The host stops a run it no longer hears from anyway; this is only sooner.
        for run in active.values { await stopOnHost(run) }
    }

    // MARK: Saved connections (no keys)

    private var savedURL: URL { dataDirectory.appendingPathComponent("remote-connections.json") }

    private func loadSaved() {
        guard !loaded else { return }
        loaded = true
        guard let size = try? savedURL.resourceValues(forKeys: [.fileSizeKey]).fileSize, size <= 256 * 1024,
              let data = try? Data(contentsOf: savedURL), let file = try? JSONDecoder().decode(SavedFile.self, from: data), file.version == 2 else { return }
        for item in file.connections.prefix(RemoteProtocol.maximumConnections) {
            guard CoreValidation.identifier(item.id), connections[item.id] == nil, let address = try? RemoteAddress.parse(item.address),
                  !connections.values.contains(where: { $0.info.address == address.display }) else { continue }
            connections[item.id] = Connection(info: RemoteConnectionInfo(id: item.id, name: String(item.name.prefix(120)), address: address.display,
                                                                          detail: L("remote.connect.detail.saved")), keyPending: !testing)
            order.append(item.id)
        }
    }

    private func writeSaved() {
        let file = SavedFile(connections: order.compactMap { connections[$0].map { Saved(id: $0.info.id, name: $0.info.name, address: $0.info.address) } })
        do {
            try FileManager.default.createDirectory(at: dataDirectory, withIntermediateDirectories: true)
            try JSONEncoder().encode(file).write(to: savedURL, options: .atomic)
            try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: savedURL.path)
        } catch {
            // The connection still works for this launch; only its listing is not kept.
        }
    }

    public func shutdown() async {
        closing = true
        for id in Array(runs.keys) { await stop(id: id) }
    }
}
