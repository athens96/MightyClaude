import Foundation
import CryptoKit
import Security

public actor RemoteService {
    private struct Connection: Sendable {
        var info: RemoteConnectionInfo
        var token: String?
        var target: RemoteTarget?
        /// A saved connection whose key has not been read from the Keychain yet.
        /// Reading happens on explicit use, never at launch, so macOS does not
        /// ask for Keychain access every time the app starts.
        var keychainPending = false
        var revision = UUID()
    }
    private struct ClientRun {
        var revision = UUID()
        var connectionId: String
        var target: RemoteTarget
        var token: String
        var jobId: String?
        var cursor = 0
        var stopping = false
        var task: Task<Void, Never>?
    }
    private struct HostJob {
        var events: [WireEvent] = []
        var cursor = 0
        var bytes = 0
        var lastPoll = Date()
        var done = false
        var stopping = false
        var finishedAt: Date?
        var task: Task<Void, Never>?
    }
    private struct SavedConnection: Codable { var id: String; var name: String; var address: String }
    private struct SavedConnections: Codable { var version = 1; var connections: [SavedConnection] }
    private let repository: StateRepository
    private let providers: ProviderService
    private let pluginDirectory: URL
    private let dataDirectory: URL
    private let onEvent: @Sendable (RunEvent) -> Void
    private let testing: Bool
    private let hostId = UUID().uuidString
    private var connections: [String: Connection] = [:]
    private var clientRuns: [String: ClientRun] = [:]
    private var jobs: [String: HostJob] = [:]
    private var host = RemoteHostState()
    private var server: HTTPServer?
    private var hostRunner: ProcessRunner?
    private var hostEvents: Task<Void, Never>?
    private var leaseTask: Task<Void, Never>?
    private var hostRevision = UUID()
    private var changingHost = false
    private var stoppingHosts = 0
    private var connecting = false
    private var disposed = false
    private var loaded = false
    private var probe = TailscaleProbe(state: .init(), peers: [])
    private var probeTime = Date.distantPast
    private var rateTime = Date.distantPast
    private var rateCount = 0
    private var peerRates: [String: (Date, Int)] = [:]

    public init(repository: StateRepository, providers: ProviderService, pluginDirectory: URL, dataDirectory: URL, onEvent: @escaping @Sendable (RunEvent) -> Void, allowLoopbackForTests: Bool = false) {
        self.repository = repository; self.providers = providers; self.pluginDirectory = pluginDirectory; self.dataDirectory = dataDirectory; self.onEvent = onEvent; self.testing = allowLoopbackForTests
    }

    private func ensureActive() throws { if disposed { throw RemoteFailure("앱이 종료 중입니다.") } }

    private func discover(force: Bool = false) async -> TailscaleProbe {
        if testing { return TailscaleProbe(state: .init(available: true, addresses: ["127.0.0.1"], deviceName: "Loopback test", detail: "격리된 루프백 테스트"), peers: ["127.0.0.1"]) }
        if !force && Date().timeIntervalSince(probeTime) < 10 { return probe }
        let next = await TailscaleDiscovery.inspect()
        probe = next; probeTime = Date()
        return next
    }

    public func state() async -> RemoteState {
        loadConnections()
        let status = disposed ? probe : await discover()
        var visibleHost = host
        visibleHost.activeRuns = jobs.values.filter { !$0.done }.count
        return RemoteState(tailscale: status.state, host: visibleHost, connections: connections.values.map(\.info).sorted { $0.name < $1.name })
    }

    public func startSharing(workspaceIds: [String], port: Int = 43137) async throws -> RemoteState {
        try ensureActive()
        guard !changingHost, stoppingHosts == 0, !host.enabled, server == nil else { throw RemoteFailure("공유 설정을 변경 중이거나 이미 공유 중입니다.") }
        guard !workspaceIds.isEmpty, workspaceIds.count <= 64, Set(workspaceIds).count == workspaceIds.count, workspaceIds.allSatisfy(CoreValidation.identifier), (1024...65535).contains(port) || (testing && port == 0) else { throw RemoteFailure("공유할 로컬 워크스페이스와 1024–65535 포트를 선택하세요.") }
        changingHost = true
        defer { changingHost = false }
        let generation = UUID(); hostRevision = generation
        let snapshot = try await repository.load()
        for id in workspaceIds {
            guard snapshot.workspaces.contains(where: { $0.id == id && $0.remote == nil }) else { throw RemoteFailure("등록된 로컬 워크스페이스만 공유할 수 있습니다.") }
            _ = try await repository.resolveLocalWorkspace(id: id)
        }
        let tailscale = await discover(force: true)
        try ensureActive()
        guard hostRevision == generation, tailscale.state.available, let address = tailscale.state.addresses.first(where: { !$0.contains(":") }) ?? tailscale.state.addresses.first else { throw RemoteFailure("Tailscale 연결을 확인해 주세요.") }
        var random = [UInt8](repeating: 0, count: 32)
        guard SecRandomCopyBytes(kSecRandomDefault, random.count, &random) == errSecSuccess else { throw RemoteFailure("연결 키를 생성하지 못했습니다.") }
        let token = Data(random).base64EncodedString().replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_").replacingOccurrences(of: "=", with: "")
        var continuation: AsyncStream<RunEvent>.Continuation!
        let stream = AsyncStream<RunEvent> { continuation = $0 }
        let sink = continuation!
        let runner = ProcessRunner(providerService: providers, pluginDirectory: pluginDirectory, onEvent: { event in sink.yield(event) })
        hostRunner = runner
        hostEvents = Task { for await event in stream { if Task.isCancelled { break }; self.receiveHost(event, generation: generation) } }
        let allowLoopback = testing
        let server = HTTPServer(address: address, port: UInt16(port), requestBodyLimit: { head in
            Self.attachmentBodyLimit(head, token: token, allowLoopback: allowLoopback)
        }) { request in await self.serve(request, generation: generation) }
        self.server = server
        host = RemoteHostState(enabled: true, token: token, workspaceIds: workspaceIds, detail: "선택한 워크스페이스를 공유합니다.")
        do {
            let bound = try await server.start()
            guard !disposed, hostRevision == generation else { await server.stop(); await runner.shutdown(); throw RemoteFailure("공유 시작이 취소되었습니다.") }
            host.port = Int(bound)
            host.address = "http://\(address.contains(":") ? "[\(address)]" : address):\(bound)"
            leaseTask = Task {
                while !Task.isCancelled {
                    try? await Task.sleep(nanoseconds: 1_000_000_000)
                    if Task.isCancelled { break }
                    await self.expireJobs(generation: generation)
                }
            }
        } catch {
            if hostRevision == generation { _ = await stopSharing() }
            throw error
        }
        return await state()
    }

    public func stopSharing() async -> RemoteState {
        stoppingHosts += 1
        defer { stoppingHosts -= 1 }
        hostRevision = UUID()
        let oldServer = server; let oldRunner = hostRunner
        let oldEvents = hostEvents; hostEvents = nil
        server = nil; hostRunner = nil; host = .init()
        leaseTask?.cancel(); leaseTask = nil
        for job in jobs.values { job.task?.cancel() }
        jobs.removeAll()
        if let oldServer { await oldServer.stop() }
        if let oldRunner { await oldRunner.shutdown() }
        oldEvents?.cancel()
        return await state()
    }

    // Enforce bearer authentication before buffering the larger upload body.
    nonisolated static func attachmentBodyLimit(_ head: HTTPRequest, token: String, allowLoopback: Bool) -> Int {
        let ordinary = 512 * 1024
        let authorization = head.headers["authorization"] ?? ""
        guard head.method == "POST", head.target == "/v1/runs", head.headers["origin"] == nil,
              RemoteIPPolicy.allowed(head.remoteAddress, allowLoopback: allowLoopback),
              head.headers["x-mighty-remote-version"] == "1",
              authorization.hasPrefix("Bearer "), authorization.count <= 256 else { return ordinary }
        let lhs = Array(SHA256.hash(data: Data(authorization.dropFirst(7).utf8)))
        let rhs = Array(SHA256.hash(data: Data(token.utf8)))
        var difference: UInt8 = 0
        for index in lhs.indices { difference |= lhs[index] ^ rhs[index] }
        return difference == 0 ? AttachmentSupport.maximumRequestBytes : ordinary
    }

    private func authorize(_ request: HTTPRequest, generation: UUID) throws {
        guard !disposed, host.enabled, generation == hostRevision else { throw HTTPFailure(503, "공유가 종료되었습니다.") }
        guard request.headers["origin"] == nil, RemoteIPPolicy.allowed(request.remoteAddress, allowLoopback: testing) else { throw HTTPFailure(403, "Tailscale 앱 연결만 허용합니다.") }
        let now = Date()
        if now.timeIntervalSince(rateTime) >= 1 { rateTime = now; rateCount = 0 }
        rateCount += 1
        var peer = peerRates[request.remoteAddress] ?? (now, 0)
        if now.timeIntervalSince(peer.0) >= 1 { peer = (now, 0) }
        peer.1 += 1; peerRates[request.remoteAddress] = peer
        if peerRates.count > 64, let first = peerRates.keys.first { peerRates.removeValue(forKey: first) }
        guard rateCount <= 100, peer.1 <= 60 else { throw HTTPFailure(429, "요청이 너무 많습니다.") }
        let authorization = request.headers["authorization"] ?? ""
        guard authorization.hasPrefix("Bearer "), authorization.count <= 256, let token = host.token else { throw HTTPFailure(401, "연결 키가 올바르지 않습니다.") }
        let lhs = Array(SHA256.hash(data: Data(authorization.dropFirst(7).utf8)))
        let rhs = Array(SHA256.hash(data: Data(token.utf8)))
        var difference: UInt8 = 0
        for index in lhs.indices { difference |= lhs[index] ^ rhs[index] }
        guard difference == 0 else { throw HTTPFailure(401, "연결 키가 올바르지 않습니다.") }
        guard request.headers["x-mighty-remote-version"] == "1" else { throw HTTPFailure(426, "원격 프로토콜 버전이 다릅니다.") }
    }

    private struct HTTPFailure: Error { let status: Int; let message: String; init(_ status: Int, _ message: String) { self.status = status; self.message = message } }
    private func response<T: Encodable>(_ status: Int, _ value: T) -> HTTPResponse { HTTPResponse(status: status, body: (try? JSONEncoder().encode(value)) ?? Data("{}".utf8), headers: ["x-mighty-remote-version": "1"]) }
    private func errorResponse(_ status: Int, _ message: String) -> HTTPResponse {
        var result = HTTPResponse.json(status, ["protocol": 1, "error": String(message.prefix(1000))]); result.headers["x-mighty-remote-version"] = "1"; return result
    }

    private func serve(_ request: HTTPRequest, generation: UUID) async -> HTTPResponse {
        do {
            try authorize(request, generation: generation)
            guard let url = URLComponents(string: request.target), url.scheme == nil, url.host == nil else { throw HTTPFailure(400, "경로가 올바르지 않습니다.") }
            if request.method == "GET", url.path == "/v1/info", url.query == nil {
                let snapshot = try await repository.load()
                let runtime = await providers.runtimeInfo()
                guard generation == hostRevision else { throw HTTPFailure(503, "공유가 종료되었습니다.") }
                let workspaces = snapshot.workspaces.filter { host.workspaceIds.contains($0.id) && $0.remote == nil && RemoteValidation.workspace($0) }
                return response(200, WireInfo(hostId: hostId, hostName: Host.current().localizedName ?? "MightyClaude Mac", workspaces: workspaces, runtime: runtime))
            }
            if request.method == "POST", url.path == "/v1/runs", url.query == nil {
                guard request.headers["content-type"]?.split(separator: ";").first?.trimmingCharacters(in: .whitespaces) == "application/json" else { throw HTTPFailure(415, "JSON 요청이 필요합니다.") }
                let incoming: StartRunRequest
                do { incoming = try JSONDecoder().decode(WireStart.self, from: request.body).request; try CoreValidation.validate(incoming) } catch { throw HTTPFailure(400, "실행 요청이 올바르지 않습니다.") }
                guard host.workspaceIds.contains(incoming.workspaceId) else { throw HTTPFailure(403, "공유하지 않은 워크스페이스입니다.") }
                let snapshot = try await repository.load()
                guard snapshot.workspaces.contains(where: { $0.id == incoming.workspaceId && $0.remote == nil }) else { throw HTTPFailure(403, "등록되지 않은 워크스페이스입니다.") }
                let workspace = try await repository.resolveLocalWorkspace(id: incoming.workspaceId)
                guard generation == hostRevision, let runner = hostRunner else { throw HTTPFailure(503, "공유가 종료되었습니다.") }
                guard jobs.values.filter({ !$0.done }).count < 16 else { throw HTTPFailure(429, "동시 원격 실행은 16개까지 가능합니다.") }
                pruneJobs()
                let id = UUID().uuidString
                jobs[id] = HostJob()
                var run = incoming; run.sessionId = id
                let task = Task { await self.launchHost(run, workspace: workspace, runner: runner, generation: generation) }
                jobs[id]?.task = task
                return response(202, WireJob(protocol: 1, jobId: id))
            }
            let parts = url.path.split(separator: "/").map(String.init)
            guard parts.count == 4, parts[0] == "v1", parts[1] == "runs", CoreValidation.identifier(parts[2]), let job = jobs[parts[2]] else { throw HTTPFailure(404, "원격 실행 기록을 찾을 수 없습니다.") }
            let id = parts[2]
            if request.method == "GET", parts[3] == "events" {
                guard let query = url.queryItems, query.count == 1, query[0].name == "cursor", let raw = query[0].value, raw.range(of: "^[0-9]{1,12}$", options: .regularExpression) != nil, let cursor = Int(raw), cursor <= job.cursor else { throw HTTPFailure(400, "출력 위치가 올바르지 않습니다.") }
                jobs[id]?.lastPoll = Date()
                let events = Array(job.events.filter { $0.cursor > cursor }.prefix(100)).map { item -> WireEvent in
                    let unsupported = item.event.type == "activity" && request.headers["x-mighty-activity"] != "1" || item.event.type == "usage" && request.headers["x-mighty-usage"] != "1" || item.event.type == "graph" && request.headers["x-mighty-graph"] != "1"
                    guard unsupported else { return item }
                    // v1 clients without this optional header only know the
                    // original event types. Keep every cursor and the real
                    // terminal status while degrading transient activity.
                    return WireEvent(cursor: item.cursor, event: RunEvent(sessionId: item.event.sessionId, type: "status", status: "running"))
                }
                return response(200, WirePoll(cursor: events.last?.cursor ?? cursor, lastCursor: job.cursor, gap: (job.events.first?.cursor ?? (cursor + 1)) > cursor + 1, done: job.done, events: events))
            }
            if request.method == "POST", parts[3] == "stop", url.query == nil {
                await stopHostJob(id)
                return response(200, ["protocol": 1, "stopped": 1])
            }
            throw HTTPFailure(404, "원격 경로를 찾을 수 없습니다.")
        } catch let failure as HTTPFailure { return errorResponse(failure.status, failure.message) }
        catch { return errorResponse(500, error.localizedDescription) }
    }

    private func launchHost(_ request: StartRunRequest, workspace: Workspace, runner: ProcessRunner, generation: UUID) async {
        guard !Task.isCancelled, generation == hostRevision, jobs[request.sessionId]?.done == false, jobs[request.sessionId]?.stopping == false else { return }
        do { try await runner.start(request: request, workspace: workspace) }
        catch {
            guard generation == hostRevision, jobs[request.sessionId]?.done == false else { return }
            receiveHost(RunEvent(sessionId: request.sessionId, type: "log", entry: .init(kind: "error", text: error.localizedDescription)), generation: generation)
            receiveHost(RunEvent(sessionId: request.sessionId, type: "status", status: jobs[request.sessionId]?.stopping == true ? "stopped" : "error"), generation: generation)
        }
    }
    private func receiveHost(_ event: RunEvent, generation: UUID) {
        guard generation == hostRevision, var job = jobs[event.sessionId], !job.done, RemoteValidation.event(event, sessionId: event.sessionId) else { return }
        job.cursor += 1
        let wire = WireEvent(cursor: job.cursor, event: event)
        job.events.append(wire); job.bytes += (try? JSONEncoder().encode(wire).count) ?? 0
        while job.events.count > 256 || job.bytes > 512 * 1024 {
            job.bytes -= (try? JSONEncoder().encode(job.events.removeFirst()).count) ?? 0
        }
        if event.type == "status", ["completed", "error", "stopped"].contains(event.status ?? "") { job.done = true; job.finishedAt = Date() }
        jobs[event.sessionId] = job
    }
    private func stopHostJob(_ id: String) async {
        guard jobs[id]?.done == false, jobs[id]?.stopping == false else { return }
        let generation = hostRevision
        jobs[id]?.stopping = true; jobs[id]?.task?.cancel()
        await hostRunner?.stop(id: id)
        if generation == hostRevision, jobs[id]?.done == false { receiveHost(RunEvent(sessionId: id, type: "status", status: "stopped"), generation: generation) }
    }
    private func expireJobs(generation: UUID) async {
        guard generation == hostRevision else { return }
        let expired = jobs.filter { !$0.value.done && !$0.value.stopping && Date().timeIntervalSince($0.value.lastPoll) >= 20 }.map(\.key)
        for id in expired { await stopHostJob(id) }
        pruneJobs()
    }
    private func pruneJobs() {
        for (id, job) in jobs where job.done && Date().timeIntervalSince(job.finishedAt ?? Date()) > 120 { jobs.removeValue(forKey: id) }
        while jobs.count >= 32, let oldest = jobs.filter({ $0.value.done }).min(by: { ($0.value.finishedAt ?? .distantPast) < ($1.value.finishedAt ?? .distantPast) }) { jobs.removeValue(forKey: oldest.key) }
    }

    public func connectRemote(name: String, address: String, token: String) async throws -> RemoteState {
        try ensureActive(); loadConnections()
        guard !connecting else { throw RemoteFailure("연결을 설정 중입니다.") }
        guard !name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, name.count <= 120, RemoteValidation.token(token) else { throw RemoteFailure("연결 이름과 호스트의 연결 키를 확인하세요.") }
        let parsed = try ParsedRemoteAddress.parse(address)
        let existing = connections.values.first { $0.info.address == parsed.origin }
        guard existing != nil || connections.count < 16 else { throw RemoteFailure("연결은 16개까지 저장할 수 있습니다.") }
        connecting = true; defer { connecting = false }
        let tailscale = await discover(force: true)
        guard tailscale.state.available else { throw RemoteFailure("이 컴퓨터의 Tailscale 연결을 확인해 주세요.") }
        let target = try await RemoteTransport.resolve(parsed, peers: tailscale.peers, allowLoopback: testing)
        let data = try await RemoteTransport.request(target, token: token, method: "GET", path: "/v1/info")
        let info = try JSONDecoder().decode(WireInfo.self, from: data); try RemoteValidation.info(info)
        try ensureActive()
        if let existing, connections[existing.info.id]?.revision != existing.revision { throw RemoteFailure("연결 설정이 변경되었습니다. 다시 연결해 주세요.") }
        if let existing {
            let reservation = UUID()
            await disconnectRuns(id: existing.info.id, revision: reservation)
            try ensureActive()
            guard connections[existing.info.id]?.revision == reservation else { throw RemoteFailure("연결 설정이 변경되었습니다. 다시 연결해 주세요.") }
        }
        let id = existing?.info.id ?? UUID().uuidString
        let saved = !testing && RemoteKeychain.save(token, id: id)
        let view = RemoteConnectionInfo(id: id, name: name, address: parsed.origin, status: "connected", hostId: info.hostId, hostName: info.hostName, workspaces: info.workspaces, runtime: info.runtime, detail: saved ? "연결됨 · 연결 키는 Keychain에 저장했습니다." : "연결됨 · 연결 키는 현재 앱 실행 중에만 유지합니다.")
        connections[id] = Connection(info: view, token: token, target: target)
        saveConnections()
        return await state()
    }

    public func refreshRemote(id: String) async throws -> RemoteState {
        try ensureActive(); loadConnections()
        if connections[id]?.token == nil, connections[id]?.keychainPending == true {
            connections[id]?.token = RemoteKeychain.load(id)
            connections[id]?.keychainPending = false
        }
        guard let current = connections[id], let token = current.token else { throw RemoteFailure("연결 이름·주소·키를 입력해 다시 연결해 주세요.") }
        let tailscale = await discover(force: true)
        do {
            guard tailscale.state.available else { throw RemoteFailure("Tailscale 연결이 끊겼습니다.") }
            let target = try await RemoteTransport.resolve(ParsedRemoteAddress.parse(current.info.address), peers: tailscale.peers, allowLoopback: testing)
            let data = try await RemoteTransport.request(target, token: token, method: "GET", path: "/v1/info")
            let info = try JSONDecoder().decode(WireInfo.self, from: data); try RemoteValidation.info(info)
            guard !disposed, connections[id]?.revision == current.revision else { throw RemoteFailure("연결 설정이 변경되었습니다.") }
            connections[id]?.target = target
            connections[id]?.info.status = "connected"; connections[id]?.info.hostId = info.hostId; connections[id]?.info.hostName = info.hostName
            connections[id]?.info.workspaces = info.workspaces; connections[id]?.info.runtime = info.runtime; connections[id]?.info.detail = "연결됨"
            return await state()
        } catch {
            if connections[id]?.revision == current.revision { await failConnection(id, message: error.localizedDescription) }
            throw error
        }
    }

    public func disconnectRemote(id: String) async -> RemoteState {
        await disconnectRuns(id: id, revision: UUID())
        return await state()
    }
    private func disconnectRuns(id: String, revision: UUID) async {
        connections[id]?.revision = revision; connections[id]?.info.status = "disconnected"; connections[id]?.info.detail = "연결을 해제했습니다. 새로고침하면 다시 연결합니다."
        for run in clientRuns.filter({ $0.value.connectionId == id }).map(\.key) { await stop(id: run) }
    }

    public func importWorkspace(connectionId: String, workspaceId: String) async throws -> Workspace {
        try ensureActive()
        guard let connection = connections[connectionId], connection.info.status == "connected", let peer = connection.info.workspaces.first(where: { $0.id == workspaceId }), RemoteValidation.workspace(peer) else { throw RemoteFailure("연결된 원격 워크스페이스를 찾을 수 없습니다.") }
        return try await repository.approveRemoteWorkspace(connectionId: connectionId, workspace: peer, hostName: connection.info.name)
    }

    public func start(request: StartRunRequest, workspace: Workspace) async throws {
        try Task.checkCancellation()
        try ensureActive(); try CoreValidation.validate(request)
        guard clientRuns[request.sessionId] == nil, clientRuns.count < 16 else { throw RemoteFailure("이 창이 이미 실행 중이거나 동시 실행 한도에 도달했습니다.") }
        guard workspace.id == request.workspaceId, let ref = workspace.remote, let connection = connections[ref.connectionId], connection.info.status == "connected", let target = connection.target, let token = connection.token, connection.info.workspaces.contains(where: { $0.id == ref.workspaceId }) else { throw RemoteFailure("원격 워크스페이스 연결을 확인해 주세요.") }
        if request.kind == "claude" {
            guard let provider = connection.info.runtime?.providers?.first(where: { $0.id == request.provider }) else { throw RemoteFailure("원격 실행기의 지원 설정을 확인할 수 없습니다.") }
            try CoreValidation.validateCapabilities(request, capabilities: provider.capabilities)
        }
        let run = ClientRun(connectionId: ref.connectionId, target: target, token: token)
        clientRuns[request.sessionId] = run
        var incoming = request; incoming.workspaceId = ref.workspaceId
        do {
            let encoder = JSONEncoder(); encoder.outputFormatting = [.withoutEscapingSlashes]
            let body = try encoder.encode(WireStart(request: incoming))
            guard body.count <= AttachmentSupport.maximumRequestBytes else { throw RemoteFailure("첨부 요청 크기 제한을 초과했습니다.") }
            let data = try await RemoteTransport.request(target, token: token, method: "POST", path: "/v1/runs", body: body, timeout: request.attachments.isEmpty ? 12 : 60)
            let accepted = try JSONDecoder().decode(WireJob.self, from: data)
            guard accepted.protocol == 1, CoreValidation.identifier(accepted.jobId) else { throw RemoteFailure("원격 실행 ID가 올바르지 않습니다.") }
            guard !disposed, !Task.isCancelled, clientRuns[request.sessionId]?.revision == run.revision, clientRuns[request.sessionId]?.stopping == false else {
                var cancelled = run; cancelled.jobId = accepted.jobId; await bestEffortStop(cancelled)
                if !request.attachments.isEmpty { throw CancellationError() }; return
            }
            clientRuns[request.sessionId]?.jobId = accepted.jobId
            onEvent(RunEvent(sessionId: request.sessionId, type: "status", status: "running"))
            clientRuns[request.sessionId]?.task = Task { await self.poll(id: request.sessionId, revision: run.revision) }
        } catch {
            if clientRuns[request.sessionId]?.revision == run.revision { clientRuns.removeValue(forKey: request.sessionId); throw error }
            if !request.attachments.isEmpty { throw error }
        }
    }

    private func poll(id: String, revision: UUID) async {
        while !Task.isCancelled, let run = clientRuns[id], !run.stopping, run.revision == revision, let jobId = run.jobId {
            do {
                let data = try await RemoteTransport.request(run.target, token: run.token, method: "GET", path: "/v1/runs/\(jobId)/events?cursor=\(run.cursor)")
                guard !Task.isCancelled, clientRuns[id]?.revision == revision else { return }
                let poll = try JSONDecoder().decode(WirePoll.self, from: data)
                guard poll.protocol == 1, poll.cursor >= run.cursor, poll.lastCursor >= poll.cursor, poll.events.count <= 100 else { throw RemoteFailure("원격 출력 응답이 올바르지 않습니다.") }
                var cursor = run.cursor
                var terminal = false
                if poll.gap { onEvent(RunEvent(sessionId: id, type: "log", entry: .init(kind: "system", text: "원격 출력 버퍼 한도를 넘어 앞부분 일부를 생략했습니다."))) }
                for item in poll.events {
                    guard item.cursor > cursor, item.cursor <= poll.cursor, RemoteValidation.event(item.event, sessionId: jobId) else { throw RemoteFailure("원격 출력 순서가 올바르지 않습니다.") }
                    cursor = item.cursor
                    var event = item.event; event.sessionId = id
                    event.usage = event.usage.flatMap { SessionUsageSupport.normalized($0) }
                    event.graph = event.graph.flatMap { ExecutionGraphSupport.normalized($0) }
                    if (event.type != "usage" || event.usage != nil) && (event.type != "graph" || event.graph != nil) { onEvent(event) }
                    if event.type == "status", ["completed", "error", "stopped"].contains(event.status ?? "") { terminal = true }
                }
                guard cursor == poll.cursor else { throw RemoteFailure("원격 출력 위치가 올바르지 않습니다.") }
                clientRuns[id]?.cursor = cursor
                if poll.done && cursor >= poll.lastCursor {
                    guard terminal else { throw RemoteFailure("원격 종료 상태를 확인하지 못했습니다.") }
                    clientRuns.removeValue(forKey: id); return
                }
                try await Task.sleep(nanoseconds: 500_000_000)
            } catch {
                if !Task.isCancelled, clientRuns[id]?.revision == revision { await failConnection(run.connectionId, message: error.localizedDescription) }
                return
            }
        }
    }
    private func bestEffortStop(_ run: ClientRun) async {
        guard let id = run.jobId else { return }
        _ = try? await RemoteTransport.request(run.target, token: run.token, method: "POST", path: "/v1/runs/\(id)/stop", body: Data("{}".utf8), timeout: 3)
    }
    public func stop(id: String) async {
        guard let run = clientRuns[id], !run.stopping else { return }
        clientRuns[id]?.stopping = true
        run.task?.cancel()
        await bestEffortStop(run)
        guard clientRuns[id]?.revision == run.revision else { return }
        clientRuns.removeValue(forKey: id)
        onEvent(RunEvent(sessionId: id, type: "status", status: "stopped"))
    }
    private func failConnection(_ id: String, message: String) async {
        connections[id]?.revision = UUID(); connections[id]?.info.status = "disconnected"; connections[id]?.info.detail = String(message.prefix(300))
        let active = clientRuns.filter { $0.value.connectionId == id }
        for (pane, run) in active {
            run.task?.cancel(); clientRuns.removeValue(forKey: pane)
            onEvent(RunEvent(sessionId: pane, type: "log", entry: .init(kind: "error", text: "원격 연결이 끊겼습니다. \(String(message.prefix(300))) 호스트는 조회가 중단된 실행을 자동 정리합니다.")))
            onEvent(RunEvent(sessionId: pane, type: "status", status: "error"))
        }
        for run in active.values { await bestEffortStop(run) }
    }

    private func loadConnections() {
        guard !loaded else { return }; loaded = true
        let ownFile = dataDirectory.appendingPathComponent("remote-connections.json")
        let legacy = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Application Support/mighty-claude/remote/remote-connections.json")
        let file = FileManager.default.fileExists(atPath: ownFile.path) ? ownFile : legacy
        guard !testing || file == ownFile, let size = try? file.resourceValues(forKeys: [.fileSizeKey]).fileSize, size <= 256 * 1024, let data = try? Data(contentsOf: file), let saved = try? JSONDecoder().decode(SavedConnections.self, from: data), saved.version == 1 else { return }
        for item in saved.connections.prefix(16) {
            guard CoreValidation.identifier(item.id), let address = try? ParsedRemoteAddress.parse(item.address), !connections.values.contains(where: { $0.info.address == address.origin }) else { continue }
            // The Keychain is consulted only when this connection is refreshed.
            let info = RemoteConnectionInfo(id: item.id, name: String(item.name.prefix(120)), address: address.origin, detail: "저장된 연결입니다. 새로고침하여 연결하세요.")
            connections[item.id] = Connection(info: info, token: nil, keychainPending: !testing)
        }
    }
    private func saveConnections() {
        let saved = SavedConnections(connections: connections.values.map { SavedConnection(id: $0.info.id, name: $0.info.name, address: $0.info.address) })
        do {
            try FileManager.default.createDirectory(at: dataDirectory, withIntermediateDirectories: true)
            let file = dataDirectory.appendingPathComponent("remote-connections.json")
            try JSONEncoder().encode(saved).write(to: file, options: .atomic)
            try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: file.path)
        } catch { /* Runtime connection remains valid; tokens are never written in plaintext. */ }
    }

    public func shutdown() async {
        disposed = true
        for id in Array(clientRuns.keys) { await stop(id: id) }
        _ = await stopSharing()
    }
}
