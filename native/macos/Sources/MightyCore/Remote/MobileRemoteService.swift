import CryptoKit
import Foundation

/// The app-side view the mobile protocol exposes. Implemented by the app
/// store's bridge; every method is called off the main actor and hops itself.
public protocol MobileHostDelegate: AnyObject, Sendable {
    func mobileState() async -> MobileState
    func mobileSession(id: String) async -> MobileSessionDetail?
    /// Returns what actually happened — "started", "steered" or "queued" — so
    /// the phone never shows a steer that silently became a queued item.
    /// `mode` is already checked against `MobileWire.submitModes`, and the
    /// uploads the phone named are already files the composer would accept.
    func mobileSubmit(sessionId: String, text: String, mode: String?, attachments: [RunAttachment]) async throws -> String
    /// Sends a guided style's prompt through the very same path, so `accepted`
    /// means the same thing it does for `submit`.
    func mobileGuided(sessionId: String, style: String, skill: String, text: String) async throws -> String
    /// Stops the pane's run and answers whether one was in motion at all: a
    /// phone that slept through the end of a run still shows 중지, and must be
    /// told there was nothing left to stop rather than "중지 요청됨".
    func mobileStop(sessionId: String) async throws -> Bool
    func mobilePermission(sessionId: String, requestId: String, runId: String, allow: Bool) async throws
    func mobileAnswers(sessionId: String, requestId: String, runId: String, answers: [String: UserQuestionAnswer]) async throws
    func mobileCreateSession(workspaceId: String, kind: String, provider: String) async throws -> String
    func mobileRemoveQueued(sessionId: String, itemId: String) async throws
    func mobileRunNextQueued(sessionId: String) async throws
    func mobileRename(sessionId: String, title: String) async throws
    func mobileClose(sessionId: String) async throws
    func mobileEntries(sessionId: String, before: String, limit: Int) async throws -> MobileEntriesPage
    func mobileApplySettings(sessionId: String, request: MobileSettingsRequest) async throws
    func mobileCommands(sessionId: String) async throws -> [MobileCommand]
    /// Returns the body of `usage`/`help`; nil when the action has no text.
    func mobilePerformCommand(sessionId: String, action: String) async throws -> String?
}

/// A routed reply: HTTP-like status plus a JSON body.
public struct MobileReply: Sendable {
    public var status: Int
    public var body: Data
    public init(status: Int, body: Data) { self.status = status; self.body = body }
}

/// Phone access through a relay (docs/relay.md). The host dials out to the
/// relay, so no port, VPN or Tailscale is needed; every client connection is
/// end-to-end encrypted and admitted only with the pairing key. The m1 REST
/// routes are tunnelled as JSON request/response messages.
public actor MobileRemoteService {
    public static let maximumWait: TimeInterval = 10
    public static let bodyLimit = 64 * 1024
    public static let maximumClients = 32
    private let dataDirectory: URL
    private weak var delegate: MobileHostDelegate?
    private var settings = MobileRemoteSettings()
    private var key: String?
    private var keypair: RelayKeypair?
    private let hostId: String
    private var hostName: String
    private var appVersion: String
    private var detail = "모바일 리모트가 꺼져 있습니다."
    private var waiters: [String: [UUID: CheckedContinuation<Void, Never>]] = [:]
    private var revisions: [String: Int] = [:]
    private var disposed = false
    private var generation = 0
    private var controlTask: Task<Void, Never>?
    private var controlSocket: URLSessionWebSocketTask?
    private var relayConnected = false
    private var lastRelayError: String?
    private var clients: [String: RelayClientConnection] = [:]
    /// Connections still inside the handshake; capped separately so a peer who
    /// only knows the serverId cannot fill every slot by stalling.
    private var unauthenticated = Set<String>()
    public static let maximumUnauthenticated = 4
    private var statusObserver: (@Sendable (MobileHostStatus) -> Void)?
    private let session: URLSession
    private let uploads: MobileUploadStore
    private let deviceRegistry: MobileDeviceRegistry
    /// Which device each live connection authenticated as, so revoking one
    /// phone can close exactly its sockets.
    private var connectedDevices: [String: String] = [:]
    /// How many requests may hold a submit's attachments in memory at once.
    /// Eight phones asking for eight 5 MB files each would otherwise be
    /// hundreds of megabytes of base64 alive at the same moment.
    public static let concurrentAttachmentSubmits = 2
    private var submitPermits = MobileRemoteService.concurrentAttachmentSubmits
    private var submitWaiters: [CheckedContinuation<Void, Never>] = []
    /// Test seam: the two steps a revoke can fail at — rotating the key and
    /// writing the list — are otherwise only reachable by breaking the
    /// filesystem halfway through. Never set outside tests.
    var keyRotationFailure: String?

    /// The relay used when the user's own field is empty. The app passes the
    /// built-in `MobileWire.defaultRelayURL`; tests pass their own.
    private let defaultRelayURL: String
    /// The relay this host connects through right now, or nil when neither the
    /// user's field nor the default is usable.
    private var relayURL: String? { MobileRemoteSettings.effectiveRelay(user: settings.relayURL, fallback: defaultRelayURL) }

    public init(dataDirectory: URL, hostName: String, appVersion: String = "0.2.0", defaultRelayURL: String = MobileWire.defaultRelayURL) {
        self.dataDirectory = dataDirectory; self.hostName = hostName; self.appVersion = appVersion; self.defaultRelayURL = defaultRelayURL
        hostId = Self.stableHostId(dataDirectory)
        uploads = MobileUploadStore(directory: dataDirectory.appendingPathComponent("uploads", isDirectory: true))
        deviceRegistry = MobileDeviceRegistry(url: dataDirectory.appendingPathComponent("devices.json"))
        let configuration = URLSessionConfiguration.ephemeral
        configuration.waitsForConnectivity = false
        configuration.timeoutIntervalForRequest = 30
        session = URLSession(configuration: configuration)
    }

    public func attach(_ delegate: MobileHostDelegate) { self.delegate = delegate }
    /// Test seam; see `keyRotationFailure`.
    func setKeyRotationFailure(_ value: String?) { keyRotationFailure = value }
    public func setAppVersion(_ value: String) { appVersion = value }
    public func observeStatus(_ observer: @escaping @Sendable (MobileHostStatus) -> Void) { statusObserver = observer }

    private static func stableHostId(_ directory: URL) -> String {
        let url = directory.appendingPathComponent("host-id")
        if let existing = try? String(contentsOf: url, encoding: .utf8).trimmingCharacters(in: .whitespacesAndNewlines), CoreValidation.identifier(existing) { return existing }
        let value = UUID().uuidString.lowercased()
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        try? value.write(to: url, atomically: true, encoding: .utf8)
        return value
    }

    // MARK: Keys

    private var keyURL: URL { dataDirectory.appendingPathComponent("mobile-remote.key") }
    private var keypairURL: URL { dataDirectory.appendingPathComponent("relay-keypair.json") }

    /// Loads the saved pairing key or mints one. Owner-only file; the value is
    /// the only secret a phone needs, so it never enters the login Keychain.
    public func loadOrCreateKey() throws -> String {
        if let key { return key }
        if let saved = try? String(contentsOf: keyURL, encoding: .utf8).trimmingCharacters(in: .whitespacesAndNewlines), RemoteValidation.token(saved) {
            try? FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: keyURL.path)
            key = saved; return saved
        }
        return try regenerateKey()
    }
    public func regenerateKey() throws -> String {
        if let keyRotationFailure { throw MightyError(keyRotationFailure) }
        // Capture before any writes: true when an existing key is being rotated,
        // false on first-time creation where no tokens have been issued yet.
        let isRotation = key != nil || FileManager.default.fileExists(atPath: keyURL.path)
        guard let fresh = MobilePairing.generateKey() else { throw MightyError("연결 키를 생성하지 못했습니다.") }
        try FileManager.default.createDirectory(at: dataDirectory, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        try? FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: dataDirectory.path)
        let temporary = dataDirectory.appendingPathComponent("mobile-remote.key." + UUID().uuidString)
        guard FileManager.default.createFile(atPath: temporary.path, contents: Data(fresh.utf8), attributes: [.posixPermissions: 0o600]) else { throw MightyError("연결 키를 저장하지 못했습니다.") }
        if FileManager.default.fileExists(atPath: keyURL.path) { _ = try FileManager.default.replaceItemAt(keyURL, withItemAt: temporary) }
        else { try FileManager.default.moveItem(at: temporary, to: keyURL) }
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: keyURL.path)
        key = fresh
        // A new key invalidates every token issued under the old key: clear the
        // registry so no stale token can authenticate again. clearAll writes []
        // atomically; a write failure is tolerated since the key is already rotated.
        if isRotation { try? deviceRegistry.clearAll() }
        dropKeyDependentClients()
        return fresh
    }

    /// Closes connections without a token in the current registry, including
    /// pending handshakes and pairing-key clients. After a successful registry
    /// clear during rotation, this also closes formerly token-authenticated phones.
    private func dropKeyDependentClients() {
        let doomed = MobileAuthSupport.keyDependent(connections: Array(clients.keys), devices: connectedDevices, tokenHolders: deviceRegistry.tokenHolders())
        close(connections: doomed, reason: "pairing key rotated")
    }

    /// Closes the named connections and forgets what they authenticated as.
    private func close(connections: [String], reason: String) {
        for connectionId in connections {
            guard let client = clients.removeValue(forKey: connectionId) else { continue }
            connectedDevices.removeValue(forKey: connectionId)
            unauthenticated.remove(connectionId)
            Task { await client.close(reason: reason) }
        }
    }
    private func loadKeypair() throws -> RelayKeypair {
        if let keypair { return keypair }
        let loaded = try RelayKeypair.load(from: keypairURL)
        keypair = loaded
        return loaded
    }

    // MARK: Lifecycle

    public func status() -> MobileHostStatus {
        let offer = (settings.enabled && relayConnected) ? offerIfAvailable() : nil
        return MobileHostStatus(enabled: settings.enabled, relayURL: settings.relayURL, relayConnected: relayConnected, clients: clients.count,
                                serverId: hostId, publicKeyB64: (try? loadKeypair())?.publicKeyB64, key: offer?.pairingKey, pairingURL: offer?.url,
                                hostName: hostName, detail: detail, devices: deviceRegistry.infos(connected: Set(connectedDevices.values)),
                                registryWarning: deviceRegistry.warning())
    }

    /// Revoking a phone rotates the pairing key and clears the entire registry.
    /// All phones must pair again with the new key; previously issued device
    /// tokens no longer grant access. The selected phone's uploads are discarded.
    @discardableResult public func revokeDevice(_ id: String) async throws -> MobileHostStatus {
        guard deviceRegistry.contains(id) else { throw MightyError("이미 해제된 기기입니다.") }
        // A rotation that fails leaves everything as it was, including the
        // device: half a revoke must never be reported as a whole one.
        // regenerateKey() clears the whole registry so no remove() is needed.
        _ = try regenerateKey()
        // dropKeyDependentClients handles key-dependent connections; close
        // this device's token-authenticated socket explicitly.
        close(connections: connectedDevices.filter { $0.value == id }.map(\.key), reason: "device revoked")
        await uploads.discard(device: id)
        publish()
        return status()
    }
    private func offerIfAvailable() -> MobilePairingOffer? {
        guard let key = try? loadOrCreateKey(), let keypair = try? loadKeypair(), let relay = relayURL else { return nil }
        return MobilePairingOffer(serverId: hostId, publicKeyB64: keypair.publicKeyB64, relayURL: relay, pairingKey: key, name: hostName)
    }
    private func publish() { statusObserver?(status()) }

    public func apply(settings incoming: MobileRemoteSettings) async -> MobileHostStatus {
        let normalized = incoming.normalized
        let changed = normalized != settings
        let relayChanged = normalized.enabled != settings.enabled || normalized.relayURL != settings.relayURL
        let refusesLegacy = settings.allowLegacyPhones && !normalized.allowLegacyPhones
        settings = normalized
        // The switch means "no phone without a token from now on", so the ones
        // already in on the key alone go with it rather than staying until
        // they happen to reconnect.
        if refusesLegacy {
            close(connections: connectedDevices.filter { $0.value == MobileDeviceRegistry.legacyId }.map(\.key), reason: "legacy phones refused")
        }
        if settings.enabled, relayURL != nil {
            if relayChanged || controlTask == nil { await start() }
            else if changed { publish() }
        } else {
            await stop(reason: settings.enabled ? "릴레이 주소를 입력하면 연결합니다." : "모바일 리모트가 꺼져 있습니다.")
        }
        return status()
    }

    /// Keeps a control socket open to the relay and reconnects with backoff.
    private func start() async {
        guard !disposed else { return }
        await stop(reason: "")
        generation += 1
        let current = generation
        detail = "릴레이에 연결하는 중…"
        publish()
        controlTask = Task { [weak self] in
            var attempt = 0
            while !Task.isCancelled {
                guard let self, await self.generation == current else { return }
                let started = Date()
                let ok = await self.runControlSocket(generation: current)
                if Task.isCancelled { return }
                // A socket that died within seconds (e.g. evicted with 4409 by a
                // duplicate host) must back off like a failure, not retry at once.
                attempt = ok && Date().timeIntervalSince(started) > 5 ? 0 : attempt + 1
                let delay = min(30.0, pow(2.0, Double(attempt - 1)))
                await self.setDisconnected(retryIn: delay)
                try? await Task.sleep(for: .seconds(delay))
            }
        }
    }

    public func stop(reason: String = "모바일 리모트가 꺼져 있습니다.") async {
        generation += 1
        controlTask?.cancel(); controlTask = nil
        controlSocket?.cancel(with: .goingAway, reason: nil); controlSocket = nil
        let dropped = clients; clients.removeAll(); unauthenticated.removeAll(); connectedDevices.removeAll()
        for client in dropped.values { await client.close(reason: "host stopped") }
        relayConnected = false
        if !reason.isEmpty { detail = reason }
        resumeWaiters(scope: nil)
        publish()
    }

    /// Half-finished uploads are bytes nobody will ever claim, so the folder
    /// goes with the host.
    public func shutdown() async { disposed = true; await stop(reason: "앱이 종료 중입니다."); await uploads.shutdown() }

    /// Reconnects now when enabled but offline (settings sheet, network change).
    public func retryIfNeeded() async {
        guard settings.enabled, !relayConnected, !disposed, relayURL != nil else { return }
        await start()
    }

    private func setDisconnected(retryIn delay: TimeInterval) {
        relayConnected = false
        detail = (lastRelayError.map { $0 + " · " } ?? "릴레이와 연결이 끊겼습니다. ") + "\(Int(delay))초 후 다시 시도합니다."
        publish()
    }

    /// One control-socket session. Returns true when it connected at all.
    private func runControlSocket(generation current: Int) async -> Bool {
        guard let relay = relayURL, let url = RelayEndpoint.socketURL(relay: relay, serverId: hostId, role: "server", connectionId: nil) else { lastRelayError = "릴레이 주소가 올바르지 않습니다."; return false }
        let socket = session.webSocketTask(with: url)
        socket.maximumMessageSize = 1024 * 1024
        controlSocket = socket
        socket.resume()
        var connected = false
        do {
            // The relay stays silent until a phone shows up, so prove the
            // socket is open with a ping before reporting "connected".
            try await Self.ping(socket)
            connected = true; relayConnected = true; lastRelayError = nil
            detail = "휴대폰에서 QR 코드를 스캔해 연결하세요."
            publish()
            while !Task.isCancelled, generation == self.generation {
                let message = try await socket.receive()
                guard case .string(let text) = message, let data = text.data(using: .utf8),
                      let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any], let type = object["type"] as? String else { continue }
                switch type {
                case "connected":
                    if let id = object["connectionId"] as? String, Self.validConnectionId(id) { acceptClient(connectionId: id, generation: current) }
                case "disconnected":
                    if let id = object["connectionId"] as? String, let client = clients.removeValue(forKey: id) { await client.close(reason: "relay disconnected"); publish() }
                case "ping":
                    try? await socket.send(.string(#"{"type":"pong"}"#))
                default: break
                }
            }
        } catch {
            if generation == self.generation { lastRelayError = Self.describe(error, socket: socket) }
        }
        socket.cancel(with: .normalClosure, reason: nil)
        // A newer generation may already own live clients; never touch its state.
        guard generation == self.generation else { return connected }
        if !connected, lastRelayError == nil { lastRelayError = "릴레이에 연결하지 못했습니다." }
        if controlSocket === socket { controlSocket = nil }
        let dropped = clients; clients.removeAll(); unauthenticated.removeAll()
        relayConnected = false
        for client in dropped.values { await client.close(reason: "control socket closed") }
        return connected
    }

    private static func ping(_ socket: URLSessionWebSocketTask) async throws {
        let once = OnceFlag()
        try await withThrowingTaskGroup(of: Void.self) { group in
            group.addTask {
                try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
                    socket.sendPing { error in
                        guard once.claim() else { return }
                        if let error { continuation.resume(throwing: error) } else { continuation.resume() }
                    }
                }
            }
            group.addTask {
                try await Task.sleep(for: .seconds(10))
                throw MightyError("릴레이가 ping에 응답하지 않습니다.")
            }
            try await group.next()
            group.cancelAll()
        }
    }

    static func validConnectionId(_ value: String) -> Bool { value.range(of: "^[A-Za-z0-9-]{8,64}$", options: .regularExpression) != nil }

    private static func describe(_ error: Error, socket: URLSessionWebSocketTask) -> String {
        switch socket.closeCode.rawValue {
        case 4400: return "릴레이가 요청을 거부했습니다(잘못된 매개변수)."
        case 4409: return "같은 호스트 ID로 다른 앱이 릴레이에 연결했습니다."
        default:
            let nsError = error as NSError
            if nsError.domain == NSURLErrorDomain { return "릴레이 연결 오류: " + nsError.localizedDescription }
            return "릴레이 연결이 끊겼습니다."
        }
    }

    private func acceptClient(connectionId: String, generation current: Int) {
        guard clients[connectionId] == nil, clients.count < Self.maximumClients, unauthenticated.count < Self.maximumUnauthenticated,
              let delegate, let keypair = try? loadKeypair(), let pairingKey = try? loadOrCreateKey(),
              let relay = relayURL,
              let url = RelayEndpoint.socketURL(relay: relay, serverId: hostId, role: "server", connectionId: connectionId) else { return }
        let identity = RelayHostIdentity(hostId: hostId, hostName: hostName, appVersion: appVersion, pairingKey: pairingKey, keypair: keypair,
                                         devices: deviceRegistry, allowLegacy: settings.allowLegacyPhones)
        let client = RelayClientConnection(id: connectionId, url: url, session: session, identity: identity, delegate: delegate, router: self)
        clients[connectionId] = client
        unauthenticated.insert(connectionId)
        Task { [weak self] in
            await client.run()
            await self?.forget(connectionId, generation: current)
        }
        publish()
    }
    func authenticated(_ connectionId: String, deviceId: String) {
        unauthenticated.remove(connectionId)
        connectedDevices[connectionId] = deviceId
        publish()
    }
    private func forget(_ connectionId: String, generation current: Int) {
        unauthenticated.remove(connectionId)
        connectedDevices.removeValue(forKey: connectionId)
        guard current == generation else { return }
        clients.removeValue(forKey: connectionId)
        publish()
    }

    // MARK: Revisions

    /// The app calls this whenever the state or a session changed. Scope is
    /// "state" or "session:<id>"; waiters wake and connected phones are told.
    public func notify(scope: String, revision: Int) {
        if revisions[scope] == nil, revisions.count >= 512, let stale = revisions.keys.first(where: { $0 != "state" && $0 != scope }) { revisions.removeValue(forKey: stale) }
        revisions[scope] = revision
        resumeWaiters(scope: scope)
        for client in clients.values { Task { await client.notify(scope: scope, revision: revision) } }
    }
    private func resumeWaiters(scope: String?) {
        let keys = scope.map { [$0] } ?? Array(waiters.keys)
        for key in keys {
            if let pending = waiters.removeValue(forKey: key) { for continuation in pending.values { continuation.resume() } }
        }
    }
    private func wait(scope: String, beyond since: Int, seconds: TimeInterval) async {
        guard seconds > 0, (revisions[scope] ?? 0) <= since else { return }
        let id = UUID()
        let timeout = Task { [weak self] in
            try? await Task.sleep(for: .seconds(seconds))
            await self?.unregister(scope: scope, id: id)
        }
        await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
            guard (revisions[scope] ?? 0) <= since, waiters.values.reduce(0, { $0 + $1.count }) < 256 else { continuation.resume(); return }
            waiters[scope, default: [:]][id] = continuation
        }
        timeout.cancel()
    }
    private func unregister(scope: String, id: UUID) {
        if let continuation = waiters[scope]?.removeValue(forKey: id) { continuation.resume() }
    }

    // MARK: Routing (shared by the tunnel and tests)

    private struct Failure: Error { let status: Int; let message: String; init(_ status: Int, _ message: String) { self.status = status; self.message = message } }
    private func reply<T: Encodable>(_ status: Int, _ value: T) -> MobileReply { MobileReply(status: status, body: (try? JSONEncoder().encode(value)) ?? Data("{}".utf8)) }
    private func errorReply(_ status: Int, _ message: String) -> MobileReply {
        MobileReply(status: status, body: (try? JSONSerialization.data(withJSONObject: ["protocol": 1, "error": String(message.prefix(1000))])) ?? Data("{}".utf8))
    }
    private func decode<T: Decodable>(_ body: Data?, as type: T.Type, limit: Int? = nil) throws -> T {
        guard let body, body.count <= (limit ?? Self.bodyLimit) else { throw Failure(body == nil ? 400 : 413, body == nil ? "요청 본문이 필요합니다." : "요청이 너무 큽니다.") }
        do { return try JSONDecoder().decode(type, from: body) } catch { throw Failure(400, "요청 본문이 올바르지 않습니다.") }
    }
    private static func pollArguments(_ url: URLComponents) throws -> (since: Int, wait: TimeInterval) {
        var since = 0; var wait: TimeInterval = 0
        for item in url.queryItems ?? [] {
            guard let value = item.value, value.range(of: "^[0-9]{1,12}$", options: .regularExpression) != nil, let number = Int(value) else { throw Failure(400, "질의 값이 올바르지 않습니다.") }
            switch item.name {
            case "since": since = number
            case "wait": wait = min(Double(number), maximumWait)
            default: throw Failure(400, "알 수 없는 질의입니다.")
            }
        }
        return (since, wait)
    }
    private static func entriesArguments(_ url: URLComponents) throws -> (before: String, limit: Int) {
        var before: String?
        var limit = MobileWire.defaultPageLimit
        for item in url.queryItems ?? [] {
            guard let value = item.value else { throw Failure(400, "질의 값이 올바르지 않습니다.") }
            switch item.name {
            case "before":
                guard CoreValidation.identifier(value) else { throw Failure(400, "before가 올바르지 않습니다.") }
                before = value
            case "limit":
                guard let number = Int(value), (1...MobileWire.maximumPageLimit).contains(number) else { throw Failure(400, "limit은 1에서 \(MobileWire.maximumPageLimit) 사이여야 합니다.") }
                limit = number
            default: throw Failure(400, "알 수 없는 질의입니다.")
            }
        }
        guard let before else { throw Failure(400, "before가 필요합니다.") }
        return (before, limit)
    }

    /// Runs a host call and turns its refusal into the status the contract
    /// names; a plain `MightyError` keeps meaning "cannot do that now".
    private func perform<T>(_ work: () async throws -> T) async throws -> T {
        do { return try await work() }
        catch let failure as MobileHostError { throw Failure(failure.status, failure.message) }
        catch let failure as MightyError { throw Failure(409, failure.message) }
    }

    /// The trimmed text a submit or a guided request may carry. Empty is a 400
    /// unless the request brings files instead.
    private static func requestText(_ raw: String, allowEmpty: Bool) throws -> String {
        let text = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard text.utf8.count <= MobileWire.maximumText else { throw Failure(400, "요청 내용은 32 KiB 이하여야 합니다.") }
        guard allowEmpty || !text.isEmpty else { throw Failure(400, "요청 내용은 1자 이상이어야 합니다.") }
        return text
    }

    /// The upload ids a submit may name: well-formed, few enough to be inside
    /// the composer's own limit before a single byte is read back.
    private static func uploadIds(_ raw: [String]?) throws -> [String] {
        guard let raw, !raw.isEmpty else { return [] }
        guard raw.count <= AttachmentSupport.maximumCount else { throw Failure(413, "첨부 파일은 요청당 최대 \(AttachmentSupport.maximumCount)개입니다.") }
        guard raw.allSatisfy(CoreValidation.identifier) else { throw Failure(400, "첨부 식별자가 올바르지 않습니다.") }
        return raw
    }

    /// Lets at most two requests hold a submit's materialised attachments at
    /// once. A hand-rolled pair rather than a dependency: the actor already
    /// serialises the bookkeeping, so the whole semaphore is these two calls.
    private func acquireSubmitSlot() async {
        if submitPermits > 0 { submitPermits -= 1; return }
        await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
            submitWaiters.append(continuation)
        }
    }
    private func releaseSubmitSlot() {
        guard !submitWaiters.isEmpty else { submitPermits += 1; return }
        submitWaiters.removeFirst().resume()
    }

    /// Serves one m1 request. `path` carries the route and query, `body` the
    /// JSON payload of a POST. `deviceId` is the phone the connection proved
    /// itself as; uploads belong to it and to no other.
    public func route(method: String, path: String, body: Data?, deviceId: String) async -> MobileReply {
        do {
            guard let url = URLComponents(string: path), url.scheme == nil, url.host == nil else { throw Failure(400, "경로가 올바르지 않습니다.") }
            guard let delegate else { throw Failure(503, "앱이 준비되지 않았습니다.") }
            let parts = url.path.split(separator: "/").map(String.init)
            guard parts.first == "m1" else { throw Failure(404, "모바일 경로를 찾을 수 없습니다.") }
            let route = Array(parts.dropFirst())
            func sessionID(_ value: String) throws -> String {
                guard CoreValidation.identifier(value) else { throw Failure(404, "실행 창을 찾을 수 없습니다.") }
                return value
            }
            if method == "GET", route == ["info"] {
                return reply(200, MobileInfo(hostId: hostId, hostName: hostName, appVersion: appVersion))
            }
            if method == "GET", route == ["state"] {
                let poll = try Self.pollArguments(url)
                var state = await delegate.mobileState()
                if state.revision <= poll.since {
                    await wait(scope: "state", beyond: poll.since, seconds: poll.wait)
                    state = await delegate.mobileState()
                }
                return reply(200, state)
            }
            if method == "GET", route.count == 2, route[0] == "sessions" {
                let id = try sessionID(route[1])
                let poll = try Self.pollArguments(url)
                guard var detail = await delegate.mobileSession(id: id) else { throw Failure(404, "실행 창을 찾을 수 없습니다.") }
                if detail.revision <= poll.since {
                    await wait(scope: "session:" + id, beyond: poll.since, seconds: poll.wait)
                    guard let fresh = await delegate.mobileSession(id: id) else { throw Failure(404, "실행 창을 찾을 수 없습니다.") }
                    detail = fresh
                }
                return reply(200, detail)
            }
            if method == "GET", route.count == 3, route[0] == "sessions", route[2] == "entries" {
                let id = try sessionID(route[1])
                let page = try Self.entriesArguments(url)
                return reply(200, try await perform { try await delegate.mobileEntries(sessionId: id, before: page.before, limit: page.limit) })
            }
            if method == "GET", route.count == 3, route[0] == "sessions", route[2] == "commands", url.query == nil {
                let id = try sessionID(route[1])
                return reply(200, MobileCommandList(commands: try await perform { try await delegate.mobileCommands(sessionId: id) }))
            }
            if method == "POST", route.count == 4, route[0] == "sessions", route[2] == "queue", route[3] == "run-next", url.query == nil {
                let id = try sessionID(route[1])
                try await perform { try await delegate.mobileRunNextQueued(sessionId: id) }
                return reply(200, MobileOK())
            }
            if method == "POST", route.count == 5, route[0] == "sessions", route[2] == "queue", route[4] == "remove", url.query == nil {
                let id = try sessionID(route[1])
                guard CoreValidation.identifier(route[3]) else { throw Failure(404, "대기 중인 항목을 찾을 수 없습니다.") }
                try await perform { try await delegate.mobileRemoveQueued(sessionId: id, itemId: route[3]) }
                return reply(200, MobileOK())
            }
            if method == "POST", route.count == 3, route[0] == "sessions", url.query == nil {
                let id = try sessionID(route[1])
                switch route[2] {
                case "submit":
                    let request = try decode(body, as: MobileSubmitRequest.self)
                    let ids = try Self.uploadIds(request.attachments)
                    let text = try Self.requestText(request.text, allowEmpty: !ids.isEmpty)
                    if let mode = request.mode, !MobileWire.submitModes.contains(mode) { throw Failure(400, "mode는 steer 또는 queue여야 합니다.") }
                    guard !ids.isEmpty else {
                        let accepted = try await perform { try await delegate.mobileSubmit(sessionId: id, text: text, mode: request.mode, attachments: []) }
                        return reply(202, MobileSubmitResult(accepted: accepted))
                    }
                    // Reading the files back and holding them as base64 is the
                    // one place a phone can make the host allocate megabytes,
                    // so only a couple of requests are ever inside here.
                    await acquireSubmitSlot()
                    defer { releaseSubmitSlot() }
                    let claimed = try await perform { try await uploads.attachments(ids: ids, sessionId: id, deviceId: deviceId) }
                    do {
                        let accepted = try await perform { try await delegate.mobileSubmit(sessionId: id, text: text, mode: request.mode, attachments: claimed.attachments) }
                        // Spent only now: a pane that refused the request leaves
                        // the phone's uploads intact so it can simply send again.
                        await uploads.spend(claimed.claim)
                        return reply(202, MobileSubmitResult(accepted: accepted))
                    } catch {
                        await uploads.release(claimed.claim)
                        throw error
                    }
                case "guided":
                    let request = try decode(body, as: MobileGuidedRequest.self)
                    // An unregistered id and an unapproved one answer alike, so
                    // an unapproved style never shows through timing (§4.5).
                    guard let style = request.resolvedStyle, style != MobileWire.cliStyle, MightyStyleIDs.isValidShape(style) else {
                        throw Failure(400, MobileRemoteSupport.unknownStyleMessage)
                    }
                    guard let action = request.resolvedAction, action.utf8.count <= 64,
                          action.range(of: "^[A-Za-z0-9][A-Za-z0-9_.:-]{0,63}$", options: .regularExpression) != nil else {
                        throw Failure(400, "skill 이름이 올바르지 않습니다.")
                    }
                    let text = try Self.requestText(request.text ?? "", allowEmpty: true)
                    let done = try await perform { try await delegate.mobileGuided(sessionId: id, style: style, skill: action, text: text) }
                    return reply(202, MobileSubmitResult(accepted: done))
                case "uploads":
                    let request = try decode(body, as: MobileUploadRequest.self)
                    // An upload belongs to the pane that asked for it; an
                    // unknown pane is a 404 before a byte is reserved.
                    guard await delegate.mobileSession(id: id) != nil else { throw Failure(404, "실행 창을 찾을 수 없습니다.") }
                    let ticket = try await perform { try await uploads.begin(sessionId: id, deviceId: deviceId, name: request.name, size: request.size, mimeType: request.mimeType) }
                    return reply(201, ticket)
                case "stop":
                    let stopped: Bool
                    do { stopped = try await delegate.mobileStop(sessionId: id) } catch let failure as MightyError { throw Failure(409, failure.message) }
                    return reply(200, MobileStopped(stopped: stopped))
                case "permission":
                    let request = try decode(body, as: MobilePermissionAnswer.self)
                    guard CoreValidation.identifier(request.requestId), CoreValidation.identifier(request.runId) else { throw Failure(400, "권한 요청 식별자가 올바르지 않습니다.") }
                    do { try await delegate.mobilePermission(sessionId: id, requestId: request.requestId, runId: request.runId, allow: request.allow) }
                    catch let failure as MightyError { throw Failure(409, failure.message) }
                    return reply(200, MobileOK())
                case "answers":
                    let request = try decode(body, as: MobileQuestionAnswers.self)
                    guard CoreValidation.identifier(request.requestId), CoreValidation.identifier(request.runId), request.answers.count <= 16 else { throw Failure(400, "답변 형식이 올바르지 않습니다.") }
                    do { try await delegate.mobileAnswers(sessionId: id, requestId: request.requestId, runId: request.runId, answers: request.answers) }
                    catch let failure as MightyError { throw Failure(409, failure.message) }
                    return reply(200, MobileOK())
                case "rename":
                    let request = try decode(body, as: MobileRenameRequest.self)
                    guard let title = MobileRemoteSupport.renameTitle(request.title) else { throw Failure(400, "이름은 앞뒤 공백을 뺀 1~\(MobileWire.maximumTitle)자여야 합니다.") }
                    try await perform { try await delegate.mobileRename(sessionId: id, title: title) }
                    return reply(200, MobileOK())
                case "close":
                    try await perform { try await delegate.mobileClose(sessionId: id) }
                    // The pane is gone, so nothing can ever claim what it was
                    // holding; the bytes go with it rather than waiting out the
                    // expiry.
                    await uploads.discard(sessionId: id)
                    return reply(200, MobileOK())
                case "settings":
                    let request = try decode(body, as: MobileSettingsRequest.self)
                    try await perform { try await delegate.mobileApplySettings(sessionId: id, request: request) }
                    return reply(200, MobileOK())
                case "command":
                    let request = try decode(body, as: MobileCommandRequest.self)
                    guard MobileWire.performedActions.contains(request.action) else { throw Failure(400, "action은 clear · usage · help 중 하나여야 합니다.") }
                    let message = try await perform { try await delegate.mobilePerformCommand(sessionId: id, action: request.action) }
                    return reply(200, MobileCommandResult(message: message))
                default: break
                }
            }
            if method == "POST", route.count >= 3, route[0] == "uploads", url.query == nil {
                guard CoreValidation.identifier(route[1]) else { throw Failure(404, "업로드를 찾을 수 없습니다.") }
                let uploadId = route[1]
                if route.count == 4, route[2] == "chunks" {
                    guard let index = Int(route[3]), route[3].range(of: "^[0-9]{1,6}$", options: .regularExpression) != nil else { throw Failure(400, "chunk 번호가 올바르지 않습니다.") }
                    // This route alone carries a base64 chunk, so it has its own
                    // body limit; the 64 KiB one would reject every full chunk.
                    let request = try decode(body, as: MobileChunkRequest.self, limit: MobileUploadStore.chunkBodyLimit)
                    guard let data = Data(base64Encoded: request.dataBase64), data.count <= MobileUploadStore.chunkSize else { throw Failure(400, "chunk 내용이 올바르지 않습니다.") }
                    let received = try await perform { try await uploads.append(id: uploadId, deviceId: deviceId, index: index, data: data) }
                    return reply(200, MobileChunkResult(received: received))
                }
                if route.count == 3, route[2] == "complete" {
                    return reply(200, MobileUploadResult(attachment: try await perform { try await uploads.complete(id: uploadId, deviceId: deviceId) }))
                }
                if route.count == 3, route[2] == "cancel" {
                    try await perform { try await uploads.cancel(id: uploadId, deviceId: deviceId) }
                    return reply(200, MobileOK())
                }
            }
            if method == "POST", route.count == 3, route[0] == "workspaces", route[2] == "sessions", url.query == nil {
                guard CoreValidation.identifier(route[1]) else { throw Failure(404, "워크스페이스를 찾을 수 없습니다.") }
                let request = try decode(body, as: MobileCreateSessionRequest.self)
                guard ["claude", "shell"].contains(request.kind) else { throw Failure(400, "kind는 claude 또는 shell이어야 합니다.") }
                let provider = request.provider ?? "claude"
                guard ProviderOptions.ids.contains(provider) else { throw Failure(400, "지원하지 않는 실행기입니다.") }
                let created = try await perform { try await delegate.mobileCreateSession(workspaceId: route[1], kind: request.kind, provider: provider) }
                return reply(201, MobileCreatedSession(sessionId: created))
            }
            throw Failure(404, "모바일 경로를 찾을 수 없습니다.")
        } catch let failure as Failure { return errorReply(failure.status, failure.message) }
        catch { return errorReply(500, error.localizedDescription) }
    }
}

struct RelayHostIdentity: Sendable {
    let hostId: String
    let hostName: String
    let appVersion: String
    let pairingKey: String
    let keypair: RelayKeypair
    let devices: MobileDeviceRegistry
    /// Whether an app that knows nothing about device tokens may still connect.
    let allowLegacy: Bool
}

/// One phone: a relay data socket, the E2EE handshake, pairing-key check,
/// then tunnelled requests. Frames are sealed and opened on this actor so the
/// cipher counters stay ordered.
actor RelayClientConnection {
    let id: String
    private let url: URL
    private let session: URLSession
    private let identity: RelayHostIdentity
    private weak var delegate: MobileHostDelegate?
    private weak var router: MobileRemoteService?
    private var socket: URLSessionWebSocketTask?
    private var cipher: RelayCipher?
    private var authenticated = false
    /// The device this connection proved itself as; every upload it opens
    /// belongs to that device and to no other.
    private var deviceId = MobileDeviceRegistry.legacyId
    /// A socket reads exactly one auth frame, so it can hand out at most one
    /// device token. Asserted rather than assumed.
    private var issuedToken = false
    private var inFlight = 0
    private var closed = false
    private var requestTasks: [String: Task<Void, Never>] = [:]
    private var outbound: [Data] = []
    private var writer: Task<Void, Never>?
    static let handshakeDeadline: TimeInterval = 10

    init(id: String, url: URL, session: URLSession, identity: RelayHostIdentity, delegate: MobileHostDelegate, router: MobileRemoteService) {
        self.id = id; self.url = url; self.session = session; self.identity = identity; self.delegate = delegate; self.router = router
    }

    func run() async {
        let socket = session.webSocketTask(with: url)
        socket.maximumMessageSize = 1024 * 1024
        self.socket = socket
        socket.resume()
        // A peer that stalls inside the handshake must not hold a slot.
        let deadline = Task { [weak self] in
            try? await Task.sleep(for: .seconds(Self.handshakeDeadline))
            guard let self, !Task.isCancelled, await !self.authenticated else { return }
            await self.close(reason: "handshake timeout")
        }
        do {
            try await handshake(socket)
            guard let device = try await authenticate(socket) else { deadline.cancel(); await close(reason: "unauthorized"); return }
            deadline.cancel()
            await router?.authenticated(id, deviceId: device)
            let keepalive = Task { [weak self] in
                while !Task.isCancelled {
                    try? await Task.sleep(for: .seconds(20))
                    guard let self, !Task.isCancelled else { return }
                    await self.send(["type": "ping"])
                }
            }
            defer { keepalive.cancel() }
            while !closed {
                let message = try await socket.receive()
                guard case .data(let frame) = message else { continue }
                let plaintext = try openFrame(frame)
                guard let object = try? JSONSerialization.jsonObject(with: plaintext) as? [String: Any] else { continue }
                if let type = object["type"] as? String {
                    if type == "ping" { send(["type": "pong"]) }
                    continue
                }
                guard let requestId = object["id"] as? String, requestId.count <= 64, let method = object["method"] as? String, ["GET", "POST"].contains(method),
                      let path = object["path"] as? String, path.hasPrefix("/"), path.utf8.count <= 2048 else { continue }
                guard inFlight < 8 else { send(["id": requestId, "status": 429, "body": ["protocol": 1, "error": "동시 요청이 너무 많습니다."]]); continue }
                let body: Data? = (object["body"]).flatMap { try? JSONSerialization.data(withJSONObject: $0) }
                inFlight += 1
                let taskId = requestId + ":" + UUID().uuidString
                requestTasks[taskId] = Task { [weak self] in
                    guard let self else { return }
                    let reply: MobileReply
                    let device = await self.deviceId
                    if let router = await self.router { reply = await router.route(method: method, path: path, body: body, deviceId: device) }
                    else { reply = MobileReply(status: 503, body: Data(#"{"protocol":1,"error":"앱이 준비되지 않았습니다."}"#.utf8)) }
                    await self.finish(taskId: taskId, requestId: requestId, reply: reply)
                }
            }
        } catch {
            // Handshake failures, decryption errors and closed sockets all end here.
        }
        deadline.cancel()
        await close(reason: "")
    }

    private func finish(taskId: String, requestId: String, reply: MobileReply) {
        requestTasks.removeValue(forKey: taskId)
        inFlight = max(0, inFlight - 1)
        guard !Task.isCancelled else { return }
        let body = (try? JSONSerialization.jsonObject(with: reply.body)) ?? [:]
        send(["id": requestId, "status": reply.status, "body": body])
    }

    private func handshake(_ socket: URLSessionWebSocketTask) async throws {
        let message = try await socket.receive()
        guard case .string(let text) = message, let data = text.data(using: .utf8), let hello = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              hello["type"] as? String == "hello", hello["v"] as? Int == 1,
              let clientKey = (hello["clientKey"] as? String).flatMap({ Data(base64Encoded: $0) }), clientKey.count == 32,
              let clientNonce = (hello["nonce"] as? String).flatMap({ Data(base64Encoded: $0) }), clientNonce.count == 16 else { throw MightyError("핸드셰이크가 올바르지 않습니다.") }
        let serverNonce = RelayCrypto.randomBytes(16)
        let ready = try JSONSerialization.data(withJSONObject: ["type": "ready", "v": 1, "serverKey": identity.keypair.publicKeyB64, "nonce": serverNonce.base64EncodedString()], options: [.sortedKeys])
        cipher = try RelayCipher(privateKey: identity.keypair.privateKey, peerPublicKey: clientKey, clientNonce: clientNonce, serverNonce: serverNonce, isHost: true)
        try await socket.send(.string(String(decoding: ready, as: UTF8.self)))
    }

    /// The auth frame of docs/relay.md, with the device-token fields. Returns
    /// the device id the connection belongs to, or nil when it was refused.
    /// Nothing here reaches a log: the key, the token and their hashes stay in
    /// this function and the registry.
    private func authenticate(_ socket: URLSessionWebSocketTask) async throws -> String? {
        let message = try await socket.receive()
        guard case .data(let frame) = message else { return nil }
        let plaintext = try openFrame(frame)
        let object = (try? JSONSerialization.jsonObject(with: plaintext)) as? [String: Any] ?? [:]
        switch MobileAuthSupport.decide(frame: object, pairingKey: identity.pairingKey, devices: identity.devices, allowLegacy: identity.allowLegacy) {
        case .refused(let reason): return await refuse(reason)
        case .token(let device): return accept(device: device, token: nil)
        case .paired(let device, let token): return accept(device: device, token: token)
        case .legacy: return accept(device: MobileDeviceRegistry.legacyId, token: nil)
        }
    }

    private func refuse(_ reason: String) async -> String? {
        send(["type": "auth_error", "reason": reason]); await flush(); return nil
    }

    private func accept(device: String, token: String?) -> String {
        authenticated = true
        deviceId = device
        var frame: [String: Any] = ["type": "auth_ok", "hostName": identity.hostName, "hostId": identity.hostId, "appVersion": identity.appVersion]
        if let token {
            assert(!issuedToken, "one device token per socket")
            issuedToken = true
            frame["deviceToken"] = token
        }
        send(frame)
        return device
    }

    private func openFrame(_ frame: Data) throws -> Data {
        guard var cipher else { throw MightyError("암호 채널이 준비되지 않았습니다.") }
        let plaintext = try cipher.open(frame)
        self.cipher = cipher
        return plaintext
    }

    /// Seals synchronously (so counters follow call order) and hands the frame
    /// to a single writer task, which keeps the wire order equal to the
    /// counter order the client insists on.
    private func send(_ object: [String: Any]) {
        guard !closed, var cipher, let data = try? JSONSerialization.data(withJSONObject: object) else { return }
        guard let frame = try? cipher.seal(data) else { return }
        self.cipher = cipher
        outbound.append(frame)
        if writer == nil { writer = Task { [weak self] in await self?.drain() } }
    }
    private func drain() async {
        while !closed, !outbound.isEmpty, let socket {
            let frame = outbound.removeFirst()
            try? await socket.send(.data(frame))
        }
        writer = nil
    }
    /// Waits for queued frames to leave (used before an intentional close).
    private func flush() async {
        while writer != nil { try? await Task.sleep(for: .milliseconds(20)) }
    }

    func notify(scope: String, revision: Int) {
        guard authenticated else { return }
        send(["type": "notify", "scope": scope, "revision": revision])
    }

    func close(reason: String) async {
        guard !closed else { return }
        closed = true
        for task in requestTasks.values { task.cancel() }
        requestTasks.removeAll()
        outbound.removeAll()
        socket?.cancel(with: .normalClosure, reason: reason.isEmpty ? nil : Data(reason.utf8))
        socket = nil
    }
}

/// Lets a callback that may fire more than once resume a continuation exactly once.
final class OnceFlag: @unchecked Sendable {
    private let lock = NSLock()
    private var claimed = false
    func claim() -> Bool { lock.lock(); defer { lock.unlock() }; if claimed { return false }; claimed = true; return true }
}
