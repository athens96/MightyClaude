import Foundation
import CryptoKit

/// The app-side view the mobile protocol exposes. Implemented by the app
/// store's bridge; every method is called off the main actor and hops itself.
public protocol MobileHostDelegate: AnyObject, Sendable {
    func mobileState() async -> MobileState
    func mobileSession(id: String) async -> MobileSessionDetail?
    /// Returns "started", "steered" or "queued"; throws when the pane cannot run.
    func mobileSubmit(sessionId: String, text: String) async throws -> String
    func mobileStop(sessionId: String) async throws
    func mobilePermission(sessionId: String, requestId: String, runId: String, allow: Bool) async throws
    func mobileAnswers(sessionId: String, requestId: String, runId: String, answers: [String: UserQuestionAnswer]) async throws
    func mobileCreateSession(workspaceId: String, kind: String, provider: String) async throws -> String
}

/// Tailscale-only HTTP listener for phones. Long-polls resolve when the app
/// reports a new revision; the key persists in the data folder so a paired
/// phone survives app restarts (unlike the desktop-to-desktop share).
public actor MobileRemoteService {
    public static let maximumWait: TimeInterval = 10
    public static let bodyLimit = 64 * 1024
    private let dataDirectory: URL
    private let testing: Bool
    private weak var delegate: MobileHostDelegate?
    private var server: HTTPServer?
    private var generation = UUID()
    private var settings = MobileRemoteSettings()
    private var key: String?
    private var address: String?
    private var boundPort: Int?
    private var serverGeneration: UUID?
    private var detail = "모바일 리모트가 꺼져 있습니다."
    private var tailscale = TailscaleState()
    private var hostName: String
    private let hostId: String
    private var appVersion: String
    private var waiters: [String: [UUID: CheckedContinuation<Void, Never>]] = [:]
    private var revisions: [String: Int] = [:]
    private var peerRates: [String: (Date, Int)] = [:]
    private var disposed = false

    public init(dataDirectory: URL, hostName: String, appVersion: String = "0.1.0", allowLoopbackForTests: Bool = false) {
        self.dataDirectory = dataDirectory; self.hostName = hostName; self.appVersion = appVersion; self.testing = allowLoopbackForTests
        hostId = Self.stableHostId(dataDirectory)
    }

    public func attach(_ delegate: MobileHostDelegate) { self.delegate = delegate }
    public func setAppVersion(_ value: String) { appVersion = value }

    private static func stableHostId(_ directory: URL) -> String {
        let url = directory.appendingPathComponent("host-id")
        if let existing = try? String(contentsOf: url, encoding: .utf8).trimmingCharacters(in: .whitespacesAndNewlines), CoreValidation.identifier(existing) { return existing }
        let value = UUID().uuidString
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        try? value.write(to: url, atomically: true, encoding: .utf8)
        return value
    }

    // MARK: Key

    private var keyURL: URL { dataDirectory.appendingPathComponent("mobile-remote.key") }
    /// Loads the saved key or mints one. The file is owner-only; the value is
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
        guard let fresh = MobilePairing.generateKey() else { throw MightyError("연결 키를 생성하지 못했습니다.") }
        // Owner-only from the first byte: create the temp file with 0600 and
        // swap it in, rather than chmod after a world-readable write.
        try FileManager.default.createDirectory(at: dataDirectory, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        try? FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: dataDirectory.path)
        let temporary = dataDirectory.appendingPathComponent("mobile-remote.key." + UUID().uuidString)
        guard FileManager.default.createFile(atPath: temporary.path, contents: Data(fresh.utf8), attributes: [.posixPermissions: 0o600]) else { throw MightyError("연결 키를 저장하지 못했습니다.") }
        if FileManager.default.fileExists(atPath: keyURL.path) { _ = try FileManager.default.replaceItemAt(keyURL, withItemAt: temporary) }
        else { try FileManager.default.moveItem(at: temporary, to: keyURL) }
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: keyURL.path)
        key = fresh
        generation = UUID() // Old bearer tokens stop working immediately.
        return fresh
    }

    // MARK: Lifecycle

    public func status() -> MobileHostStatus {
        let listening = server != nil && boundPort != nil
        let pairing = listening ? key.flatMap { key in address.map { MobilePairing.url(host: $0, port: boundPort ?? settings.port, key: key, name: hostName) } } : nil
        return MobileHostStatus(enabled: settings.enabled, listening: listening, address: address.map { host in "http://\(host.contains(":") ? "[\(host)]" : host):\(boundPort ?? settings.port)" },
                                port: boundPort ?? settings.port, key: listening ? key : nil, pairingURL: pairing, hostName: hostName, detail: detail, tailscale: tailscale)
    }

    public func apply(settings incoming: MobileRemoteSettings) async -> MobileHostStatus {
        settings = testing && incoming.port == 0 ? incoming : incoming.normalized
        if settings.enabled { await start() } else { await stop(reason: "모바일 리모트가 꺼져 있습니다.") }
        return status()
    }

    /// Opens the listener on the first Tailscale IP. Safe to call again: a
    /// running listener on the same port is kept, a changed port restarts it.
    @discardableResult public func start() async -> MobileHostStatus {
        guard !disposed else { return status() }
        // A rotated key changes the generation; the old listener must go.
        if server != nil, boundPort == settings.port, serverGeneration == generation { return status() }
        await stop(reason: "")
        let probe = testing
            ? TailscaleProbe(state: .init(available: true, addresses: ["127.0.0.1"], deviceName: "Loopback test", detail: "격리된 루프백 테스트"), peers: ["127.0.0.1"])
            : await TailscaleDiscovery.inspect()
        tailscale = probe.state
        guard probe.state.available, let host = probe.state.addresses.first(where: { !$0.contains(":") }) ?? probe.state.addresses.first else {
            detail = "Tailscale이 연결되면 자동으로 켜집니다. " + probe.state.detail; return status()
        }
        let token: String
        do { token = try loadOrCreateKey() } catch { detail = error.localizedDescription; return status() }
        let current = generation
        let allowLoopback = testing
        let server = HTTPServer(address: host, port: UInt16(testing && settings.port == 0 ? 0 : settings.port), requestBodyLimit: { _ in Self.bodyLimit }) { request in
            await self.serve(request, token: token, generation: current, allowLoopback: allowLoopback)
        }
        do {
            let bound = try await server.start()
            guard !disposed, generation == current else { await server.stop(); return status() }
            self.server = server; boundPort = Int(bound); address = host; serverGeneration = current
            detail = "휴대폰에서 QR 코드를 스캔해 연결하세요."
        } catch {
            detail = "포트 \(settings.port)을 열지 못했습니다: \(error.localizedDescription)"
        }
        return status()
    }

    public func stop(reason: String = "모바일 리모트가 꺼져 있습니다.") async {
        if let server { await server.stop() }
        server = nil; boundPort = nil; address = nil; serverGeneration = nil
        if !reason.isEmpty { detail = reason }
        resumeWaiters(scope: nil)
    }

    public func shutdown() async { disposed = true; await stop(reason: "앱이 종료 중입니다."); }

    /// Re-checks Tailscale when the listener is enabled but not open yet.
    public func retryIfNeeded() async { if settings.enabled, server == nil, !disposed { await start() } }

    // MARK: Revisions

    /// The app calls this whenever the state or a session changed. Scope is
    /// "state" or "session:<id>"; waiters of that scope wake up.
    public func notify(scope: String, revision: Int) {
        revisions[scope] = revision
        resumeWaiters(scope: scope)
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
        // Registration happens synchronously on the actor, so the timeout's
        // unregister can never run before it; every continuation is removed
        // from `waiters` by exactly one of notify / stop / timeout.
        let timeout = Task { [weak self] in
            try? await Task.sleep(for: .seconds(seconds))
            await self?.unregister(scope: scope, id: id)
        }
        await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
            guard server != nil, (revisions[scope] ?? 0) <= since, waiters.values.reduce(0, { $0 + $1.count }) < 256 else { continuation.resume(); return }
            waiters[scope, default: [:]][id] = continuation
        }
        timeout.cancel()
    }
    private func unregister(scope: String, id: UUID) {
        if let continuation = waiters[scope]?.removeValue(forKey: id) { continuation.resume() }
    }

    // MARK: Requests

    private struct HTTPFailure: Error { let status: Int; let message: String; init(_ status: Int, _ message: String) { self.status = status; self.message = message } }
    private func response<T: Encodable>(_ status: Int, _ value: T) -> HTTPResponse {
        HTTPResponse(status: status, body: (try? JSONEncoder().encode(value)) ?? Data("{}".utf8), headers: ["x-mighty-mobile-version": "1"])
    }
    private func errorResponse(_ status: Int, _ message: String) -> HTTPResponse {
        var result = HTTPResponse.json(status, ["protocol": 1, "error": String(message.prefix(1000))]); result.headers["x-mighty-mobile-version"] = "1"; return result
    }

    private func authorize(_ request: HTTPRequest, token: String, generation current: UUID, allowLoopback: Bool) throws {
        guard generation == current, server != nil else { throw HTTPFailure(503, "모바일 리모트가 종료되었습니다.") }
        let peer = request.remoteAddress
        guard RemoteIPPolicy.allowed(peer, allowLoopback: allowLoopback) else { throw HTTPFailure(403, "Tailscale 네트워크의 연결만 허용합니다.") }
        guard request.headers["origin"] == nil, request.headers["sec-fetch-site"] == nil else { throw HTTPFailure(403, "브라우저 요청은 허용하지 않습니다.") }
        let now = Date()
        let rate = peerRates[peer] ?? (now, 0)
        let count = now.timeIntervalSince(rate.0) < 1 ? rate.1 + 1 : 1
        peerRates[peer] = (count == 1 ? now : rate.0, count)
        if peerRates.count > 64 { peerRates = peerRates.filter { now.timeIntervalSince($0.value.0) < 1 } }
        guard count <= 30 else { throw HTTPFailure(429, "요청이 너무 많습니다.") }
        guard let header = request.headers["authorization"], header.hasPrefix("Bearer ") else { throw HTTPFailure(401, "연결 키가 필요합니다.") }
        let presented = String(header.dropFirst(7))
        let left = Data(SHA256.hash(data: Data(presented.utf8))), right = Data(SHA256.hash(data: Data(token.utf8)))
        guard presented.utf8.count <= 256, left == right else { throw HTTPFailure(401, "연결 키가 올바르지 않습니다.") }
        guard request.headers["x-mighty-mobile-version"] == "1" else { throw HTTPFailure(426, "모바일 프로토콜 버전이 다릅니다.") }
    }

    private func decode<T: Decodable>(_ request: HTTPRequest, as type: T.Type) throws -> T {
        guard request.headers["content-type"]?.split(separator: ";").first?.trimmingCharacters(in: .whitespaces) == "application/json" else { throw HTTPFailure(415, "JSON 요청이 필요합니다.") }
        guard request.body.count <= Self.bodyLimit else { throw HTTPFailure(413, "요청이 너무 큽니다.") }
        do { return try JSONDecoder().decode(type, from: request.body) } catch { throw HTTPFailure(400, "요청 본문이 올바르지 않습니다.") }
    }

    private static func pollArguments(_ url: URLComponents) throws -> (since: Int, wait: TimeInterval) {
        var since = 0; var wait: TimeInterval = 0
        for item in url.queryItems ?? [] {
            guard let value = item.value, value.range(of: "^[0-9]{1,12}$", options: .regularExpression) != nil, let number = Int(value) else { throw HTTPFailure(400, "질의 값이 올바르지 않습니다.") }
            switch item.name {
            case "since": since = number
            case "wait": wait = min(Double(number), maximumWait)
            default: throw HTTPFailure(400, "알 수 없는 질의입니다.")
            }
        }
        return (since, wait)
    }

    private func serve(_ request: HTTPRequest, token: String, generation current: UUID, allowLoopback: Bool) async -> HTTPResponse {
        do {
            try authorize(request, token: token, generation: current, allowLoopback: allowLoopback)
            guard let url = URLComponents(string: request.target), url.scheme == nil, url.host == nil else { throw HTTPFailure(400, "경로가 올바르지 않습니다.") }
            guard let delegate else { throw HTTPFailure(503, "앱이 준비되지 않았습니다.") }
            let parts = url.path.split(separator: "/").map(String.init)
            guard parts.first == "m1" else { throw HTTPFailure(404, "모바일 경로를 찾을 수 없습니다.") }
            let route = Array(parts.dropFirst())
            func sessionID(_ value: String) throws -> String {
                guard CoreValidation.identifier(value) else { throw HTTPFailure(404, "실행 창을 찾을 수 없습니다.") }
                return value
            }
            if request.method == "GET", route == ["info"] {
                return response(200, MobileInfo(hostId: hostId, hostName: hostName, appVersion: appVersion))
            }
            if request.method == "GET", route == ["state"] {
                let poll = try Self.pollArguments(url)
                var state = await delegate.mobileState()
                if state.revision <= poll.since {
                    await wait(scope: "state", beyond: poll.since, seconds: poll.wait)
                    state = await delegate.mobileState()
                }
                return response(200, state)
            }
            if request.method == "GET", route.count == 2, route[0] == "sessions" {
                let id = try sessionID(route[1])
                let poll = try Self.pollArguments(url)
                guard var detail = await delegate.mobileSession(id: id) else { throw HTTPFailure(404, "실행 창을 찾을 수 없습니다.") }
                if detail.revision <= poll.since {
                    await wait(scope: "session:" + id, beyond: poll.since, seconds: poll.wait)
                    guard let fresh = await delegate.mobileSession(id: id) else { throw HTTPFailure(404, "실행 창을 찾을 수 없습니다.") }
                    detail = fresh
                }
                return response(200, detail)
            }
            if request.method == "POST", route.count == 3, route[0] == "sessions", url.query == nil {
                let id = try sessionID(route[1])
                switch route[2] {
                case "submit":
                    let body = try decode(request, as: MobileSubmitRequest.self)
                    let text = body.text.trimmingCharacters(in: .whitespacesAndNewlines)
                    guard !text.isEmpty, text.utf8.count <= 32_768 else { throw HTTPFailure(400, "요청 내용은 1자 이상 32 KiB 이하여야 합니다.") }
                    do { return response(202, MobileSubmitResult(accepted: try await delegate.mobileSubmit(sessionId: id, text: text))) }
                    catch let failure as MightyError { throw HTTPFailure(409, failure.message) }
                case "stop":
                    do { try await delegate.mobileStop(sessionId: id) } catch let failure as MightyError { throw HTTPFailure(409, failure.message) }
                    return response(200, MobileStopped())
                case "permission":
                    let body = try decode(request, as: MobilePermissionAnswer.self)
                    guard CoreValidation.identifier(body.requestId), CoreValidation.identifier(body.runId) else { throw HTTPFailure(400, "권한 요청 식별자가 올바르지 않습니다.") }
                    do { try await delegate.mobilePermission(sessionId: id, requestId: body.requestId, runId: body.runId, allow: body.allow) }
                    catch let failure as MightyError { throw HTTPFailure(409, failure.message) }
                    return response(200, MobileOK())
                case "answers":
                    let body = try decode(request, as: MobileQuestionAnswers.self)
                    guard CoreValidation.identifier(body.requestId), CoreValidation.identifier(body.runId), body.answers.count <= 16 else { throw HTTPFailure(400, "답변 형식이 올바르지 않습니다.") }
                    do { try await delegate.mobileAnswers(sessionId: id, requestId: body.requestId, runId: body.runId, answers: body.answers) }
                    catch let failure as MightyError { throw HTTPFailure(409, failure.message) }
                    return response(200, MobileOK())
                default: break
                }
            }
            if request.method == "POST", route.count == 3, route[0] == "workspaces", route[2] == "sessions", url.query == nil {
                guard CoreValidation.identifier(route[1]) else { throw HTTPFailure(404, "워크스페이스를 찾을 수 없습니다.") }
                let body = try decode(request, as: MobileCreateSessionRequest.self)
                guard ["claude", "shell"].contains(body.kind) else { throw HTTPFailure(400, "kind는 claude 또는 shell이어야 합니다.") }
                let provider = body.provider ?? "claude"
                guard ProviderOptions.ids.contains(provider) else { throw HTTPFailure(400, "지원하지 않는 실행기입니다.") }
                do { return response(201, MobileCreatedSession(sessionId: try await delegate.mobileCreateSession(workspaceId: route[1], kind: body.kind, provider: provider))) }
                catch let failure as MightyError { throw HTTPFailure(409, failure.message) }
            }
            throw HTTPFailure(404, "모바일 경로를 찾을 수 없습니다.")
        } catch let failure as HTTPFailure { return errorResponse(failure.status, failure.message) }
        catch { return errorResponse(500, error.localizedDescription) }
    }
}
