import Foundation
import Network

/// The direct phone path (docs/relay.md, "Tailscale 직접 연결"): a phone on the
/// same tailnet opens its data socket straight to this Mac instead of to the
/// relay. The Mac then plays a relay that knows exactly one host — itself — and
/// everything after the socket opens is the relay path unchanged: the same
/// `hello`/`ready` handshake, the same ChaCha20-Poly1305 frames, the same auth.
public enum MobileDirect {
    /// The listener's port unless Settings changes it. Nothing else in the app uses it.
    public static let defaultPort = 43139
    /// The relay's own limits, kept for the socket that replaces it.
    public static let maximumFrameBytes = 1024 * 1024
    /// Phones still inside the handshake on the direct path, counted apart from
    /// the relay's, so a tailnet peer that stalls cannot keep relay phones out.
    public static let maximumUnauthenticated = 4
    /// Of those, at most this many from one remote address.
    public static let maximumUnauthenticatedPerPeer = 2
    /// Failed authentications one remote address may have inside
    /// `authFailureWindow` before its new sockets are dropped unread.
    public static let maximumAuthFailures = 5
    static let authFailureWindow: TimeInterval = 60
    /// The one queue every direct listener and socket runs on.
    static let queue = DispatchQueue(label: "dev.mightyclaude.mobile-direct")
    /// At most this many endpoints travel in a QR or an `auth_ok`.
    public static let maximumEndpoints = 8
    /// How often the Tailscale addresses are read again while the path is on, on
    /// top of every network change: Tailscale coming up does not always move the
    /// default route, so a path report alone can miss it.
    static let redetectInterval: TimeInterval = 15
    /// How long one bind may take before it counts as failed.
    static let bindTimeout: TimeInterval = 5

    public static func validPort(_ port: Int) -> Bool { (1024...65535).contains(port) }

    // MARK: Addresses

    /// A MagicDNS name: lowercase DNS labels under `ts.net`.
    public static func isTailnetName(_ text: String) -> Bool {
        text.count <= 253 && text.range(of: "^([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\\.)+ts\\.net$", options: .regularExpression) != nil
    }

    /// This node's MagicDNS name, read with `tailscale status --json` when the
    /// CLI is installed. Nil without the CLI, with MagicDNS off, or on any error:
    /// the addresses alone are enough.
    static func magicDNSName() async -> String? {
        // The known absolute places first; then PATH, absolute entries only, so a
        // relative entry can never run a `tailscale` from the working folder.
        let search = (ProcessInfo.processInfo.environment["PATH"] ?? "").split(separator: ":").map(String.init).filter { $0.hasPrefix("/") }
        var seen = Set<String>()
        let candidates = ["/Applications/Tailscale.app/Contents/MacOS/Tailscale", "/opt/homebrew/bin/tailscale", "/usr/local/bin/tailscale"] + search.map { $0 + "/tailscale" }
        for candidate in candidates where seen.insert(candidate).inserted && FileManager.default.isExecutableFile(atPath: candidate) {
            guard let result = try? await ProcessCapture.run(executable: URL(fileURLWithPath: candidate), arguments: ["status", "--json"], timeout: 5, maximumBytes: 4 * 1024 * 1024),
                  result.exitCode == 0,
                  let json = (try? JSONSerialization.jsonObject(with: result.stdout)) as? [String: Any],
                  let own = json["Self"] as? [String: Any], let raw = own["DNSName"] as? String else { continue }
            let name = raw.lowercased().trimmingCharacters(in: CharacterSet(charactersIn: "."))
            return isTailnetName(name) ? name : nil
        }
        return nil
    }

    // MARK: Endpoints

    /// `host:port`, an IPv6 literal in brackets.
    public static func endpoint(host: String, port: Int) -> String {
        host.contains(":") ? "[\(host)]:\(port)" : "\(host):\(port)"
    }

    /// Splits `host:port` (`[v6]:port`); nil when it is not one.
    static func split(_ endpoint: String) -> (host: String, port: Int)? {
        let host: String, portText: Substring
        if endpoint.hasPrefix("[") {
            guard let close = endpoint.firstIndex(of: "]"), endpoint[endpoint.index(after: close)...].hasPrefix(":") else { return nil }
            host = String(endpoint[endpoint.index(after: endpoint.startIndex)..<close])
            portText = endpoint[endpoint.index(close, offsetBy: 2)...]
        } else {
            guard let colon = endpoint.lastIndex(of: ":") else { return nil }
            host = String(endpoint[..<colon])
            portText = endpoint[endpoint.index(after: colon)...]
            guard !host.contains(":") else { return nil }
        }
        guard portText.range(of: "^[0-9]{1,5}$", options: .regularExpression) != nil, let port = Int(portText), (1...65535).contains(port) else { return nil }
        return (host, port)
    }

    /// An endpoint a phone may be sent to: a Tailscale address or a MagicDNS name.
    public static func validEndpoint(_ endpoint: String) -> Bool {
        guard endpoint.count <= 270, let (host, _) = split(endpoint) else { return false }
        return TailscaleAddresses.contains(host) || isTailnetName(host)
    }

    /// What the host advertises: the bound addresses with their ports, then the
    /// MagicDNS name on the configured port. Only valid endpoints, at most
    /// `maximumEndpoints`. `allowAnyHost` is the test seam's loopback.
    static func advertised(bound: [(host: String, port: Int)], name: String?, port: Int, allowAnyHost: Bool = false) -> [String] {
        var endpoints = bound.map { endpoint(host: $0.host, port: $0.port) }
        if let name, !bound.isEmpty { endpoints.append(endpoint(host: name, port: port)) }
        var seen = Set<String>()
        return Array(endpoints.filter { (allowAnyHost || validEndpoint($0)) && seen.insert($0).inserted }.prefix(maximumEndpoints))
    }

    /// The QR's `direct=` value: endpoints joined with commas.
    public static func joined(_ endpoints: [String]) -> String { endpoints.joined(separator: ",") }

    /// The QR's `direct=` value read back; invalid entries are dropped.
    public static func parse(_ value: String?) -> [String] {
        guard let value else { return [] }
        var seen = Set<String>()
        return Array(value.split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { validEndpoint($0) && seen.insert($0).inserted }.prefix(maximumEndpoints))
    }
}

/// One connection a direct listener delivered, and the listener slot it holds
/// until it ends.
final class DirectIncoming: @unchecked Sendable {
    let connection: NWConnection
    /// The remote IP address, nil when the endpoint is not an address.
    let peer: String?
    private let releaseSlot: @Sendable () -> Void
    private let released = OnceFlag()

    init(connection: NWConnection, releaseSlot: @escaping @Sendable () -> Void) {
        self.connection = connection; self.releaseSlot = releaseSlot
        peer = Self.remoteHost(connection.endpoint)
    }

    static func remoteHost(_ endpoint: NWEndpoint) -> String? {
        guard case .hostPort(let host, _) = endpoint else { return nil }
        switch host {
        case .ipv4(let address): return "\(address)"
        case .ipv6(let address): return "\(address)".split(separator: "%", maxSplits: 1).first.map { String($0).lowercased() }
        default: return nil
        }
    }

    /// Gives the slot back; only the first call counts.
    func release() { if released.claim() { releaseSlot() } }

    /// Drops the connection unread: never started, no close frame, no linger.
    func refuse() {
        connection.forceCancel()
        release()
    }
}

/// The listeners of the direct path, one per Tailscale address, each pinned to
/// its address and (outside tests) its `utun` interface. Never binds a wildcard
/// address. Together they hold at most `connectionLimit` connections at once:
/// each listener's `newConnectionLimit` is spent on every delivery and given
/// back when that connection ends, and a delivery over the shared count is
/// dropped before it is started.
final class MobileDirectListener: @unchecked Sendable {
    static let connectionLimit = MobileDirect.maximumUnauthenticated + MobileRemoteService.maximumClients
    private var queue: DispatchQueue { MobileDirect.queue }
    // Read and written on `queue` only.
    private var listeners: [String: (listener: NWListener, host: String, port: Int)] = [:]
    private var live = 0
    private var stopped = false
    private let onConnection: @Sendable (DirectIncoming) -> Void

    init(onConnection: @escaping @Sendable (DirectIncoming) -> Void) { self.onConnection = onConnection }

    /// Connections delivered and not yet ended, across every listener.
    var liveConnections: Int { queue.sync { live } }

    /// The WebSocket server stack the phone's data socket speaks: frames of at
    /// most 1 MiB like the relay's, pings answered by the stack. No address
    /// reuse: a second listener can never share the port.
    static func parameters(address: String, port: UInt16, interface: NWInterface?) -> NWParameters {
        let options = NWProtocolWebSocket.Options()
        options.autoReplyPing = true
        options.maximumMessageSize = MobileDirect.maximumFrameBytes
        let parameters = NWParameters.tcp
        parameters.defaultProtocolStack.applicationProtocols.insert(options, at: 0)
        parameters.allowLocalEndpointReuse = false
        if let interface { parameters.requiredInterface = interface }
        parameters.requiredLocalEndpoint = .hostPort(host: NWEndpoint.Host(address), port: NWEndpoint.Port(rawValue: port) ?? .any)
        return parameters
    }

    private static func key(_ address: String, _ port: UInt16) -> String { address + "|" + String(port) }

    /// Makes the listeners match `targets`: stops the ones no longer wanted,
    /// keeps the ones already up (and the phones on them), binds the rest.
    /// Answers every listener up afterwards with its port, and the first error.
    func update(targets: [(address: String, interface: NWInterface?)], port: UInt16) async -> (bound: [(host: String, port: Int)], error: String?) {
        let wanted = Set(targets.map { Self.key($0.address, port) })
        let missing: [(address: String, interface: NWInterface?)] = queue.sync {
            for (key, entry) in listeners where !wanted.contains(key) {
                entry.listener.stateUpdateHandler = nil
                entry.listener.newConnectionHandler = nil
                entry.listener.cancel()
                listeners.removeValue(forKey: key)
            }
            return targets.filter { listeners[Self.key($0.address, port)] == nil }
        }
        var failure: String?
        for target in missing {
            if case .failure(let error) = await bind(address: target.address, port: port, interface: target.interface), failure == nil {
                failure = error.localizedDescription
            }
        }
        let bound: [(host: String, port: Int)] = queue.sync {
            targets.compactMap { target in listeners[Self.key(target.address, port)].map { (host: $0.host, port: $0.port) } }
        }
        return (bound, failure)
    }

    private func bind(address: String, port: UInt16, interface: NWInterface?) async -> Result<UInt16, Error> {
        let listener: NWListener
        do { listener = try NWListener(using: Self.parameters(address: address, port: port, interface: interface)) } catch { return .failure(error) }
        listener.newConnectionLimit = Self.connectionLimit
        let once = OnceFlag()
        let key = Self.key(address, port)
        return await withCheckedContinuation { (continuation: CheckedContinuation<Result<UInt16, Error>, Never>) in
            listener.stateUpdateHandler = { [weak self, weak listener] state in
                guard let listener else { return }
                switch state {
                case .ready:
                    guard once.claim() else { return }
                    let assigned = listener.port?.rawValue ?? port
                    if let self, !self.stopped { self.listeners[key] = (listener, address, Int(assigned)) } else { listener.cancel() }
                    continuation.resume(returning: .success(assigned))
                case .failed(let error):
                    listener.cancel()
                    self?.listeners.removeValue(forKey: key)
                    if once.claim() { continuation.resume(returning: .failure(error)) }
                case .cancelled:
                    if once.claim() { continuation.resume(returning: .failure(MightyError(L("mobileRemote.direct.error.closed")))) }
                default: break
                }
            }
            listener.newConnectionHandler = { [weak self, weak listener] connection in
                guard let self, let listener else { connection.forceCancel(); return }
                self.deliver(connection, from: listener)
            }
            listener.start(queue: queue)
            queue.asyncAfter(deadline: .now() + MobileDirect.bindTimeout) {
                guard once.claim() else { return }
                listener.cancel()
                continuation.resume(returning: .failure(MightyError(L("mobileRemote.direct.error.bindTimeout"))))
            }
        }
    }

    /// On `queue`. The delivery spent one of the listener's slots; it comes
    /// back when the connection ends, however it ends.
    private func deliver(_ connection: NWConnection, from listener: NWListener) {
        live += 1
        let incoming = DirectIncoming(connection: connection) { [weak self, weak listener] in
            MobileDirect.queue.async {
                self?.live -= 1
                if let listener, listener.newConnectionLimit != NWListener.InfiniteConnectionLimit { listener.newConnectionLimit += 1 }
            }
        }
        guard !stopped, live <= Self.connectionLimit else { incoming.refuse(); return }
        onConnection(incoming)
    }

    /// Stops accepting. Connections already accepted stay up; their owners close them.
    func stop() {
        queue.sync {
            stopped = true
            for entry in listeners.values {
                entry.listener.stateUpdateHandler = nil
                entry.listener.newConnectionHandler = nil
                entry.listener.cancel()
            }
            listeners.removeAll()
        }
    }
}

/// The socket a phone's E2EE session runs on, whichever way it came in.
protocol RelayFrameSocket: AnyObject, Sendable {
    func start()
    func receive() async throws -> URLSessionWebSocketTask.Message
    func send(_ message: URLSessionWebSocketTask.Message) async throws
    func close(reason: String)
}

/// The relay path: the host dials the relay's data socket for one connection id.
final class URLSessionRelaySocket: RelayFrameSocket, @unchecked Sendable {
    private let task: URLSessionWebSocketTask
    init(task: URLSessionWebSocketTask) { self.task = task }
    func start() {
        task.maximumMessageSize = MobileDirect.maximumFrameBytes
        task.resume()
    }
    func receive() async throws -> URLSessionWebSocketTask.Message { try await task.receive() }
    func send(_ message: URLSessionWebSocketTask.Message) async throws { try await task.send(message) }
    func close(reason: String) { task.cancel(with: .normalClosure, reason: reason.isEmpty ? nil : Data(reason.utf8)) }
}

/// The direct path: a phone's WebSocket accepted by `MobileDirectListener`.
final class DirectRelaySocket: RelayFrameSocket, @unchecked Sendable {
    private let incoming: DirectIncoming
    private var connection: NWConnection { incoming.connection }
    private var queue: DispatchQueue { MobileDirect.queue }
    private let closing = OnceFlag()
    private let lock = NSLock()
    private var started = false

    init(incoming: DirectIncoming) { self.incoming = incoming }

    func start() {
        lock.lock(); started = true; lock.unlock()
        connection.stateUpdateHandler = { [incoming] state in
            switch state {
            case .failed: incoming.connection.cancel()
            case .cancelled: incoming.release()
            default: break
            }
        }
        connection.start(queue: queue)
    }

    func receive() async throws -> URLSessionWebSocketTask.Message {
        // Control frames (pings, pongs) are answered by the stack and skipped here.
        while true {
            if let message = try await receiveOne() { return message }
        }
    }

    private func receiveOne() async throws -> URLSessionWebSocketTask.Message? {
        try await withCheckedThrowingContinuation { continuation in
            connection.receiveMessage { content, context, _, error in
                if let error { continuation.resume(throwing: error); return }
                guard let metadata = context?.protocolMetadata(definition: NWProtocolWebSocket.definition) as? NWProtocolWebSocket.Metadata else {
                    continuation.resume(throwing: MightyError(L("mobileRemote.direct.error.closed"))); return
                }
                switch metadata.opcode {
                case .binary: continuation.resume(returning: .data(content ?? Data()))
                case .text:
                    guard let text = String(data: content ?? Data(), encoding: .utf8) else {
                        continuation.resume(throwing: MightyError(L("mobileRemote.direct.error.closed"))); return
                    }
                    continuation.resume(returning: .string(text))
                case .close: continuation.resume(throwing: MightyError(L("mobileRemote.direct.error.closed")))
                default: continuation.resume(returning: nil)
                }
            }
        }
    }

    func send(_ message: URLSessionWebSocketTask.Message) async throws {
        let opcode: NWProtocolWebSocket.Opcode, payload: Data
        switch message {
        case .data(let data): opcode = .binary; payload = data
        case .string(let text): opcode = .text; payload = Data(text.utf8)
        @unknown default: return
        }
        let context = NWConnection.ContentContext(identifier: "frame", metadata: [NWProtocolWebSocket.Metadata(opcode: opcode)])
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            connection.send(content: payload, contentContext: context, isComplete: true, completion: .contentProcessed { error in
                if let error { continuation.resume(throwing: error) } else { continuation.resume() }
            })
        }
    }

    func close(reason: String) { close(code: .protocolCode(.normalClosure)) }

    /// Sends a close frame with `code`, then drops the connection; a peer that
    /// never reads it is cut off two seconds later all the same.
    func close(code: NWProtocolWebSocket.CloseCode) {
        guard closing.claim() else { return }
        lock.lock(); let wasStarted = started; lock.unlock()
        // Never started: nothing to say goodbye on.
        guard wasStarted else { incoming.refuse(); return }
        let metadata = NWProtocolWebSocket.Metadata(opcode: .close)
        metadata.closeCode = code
        let context = NWConnection.ContentContext(identifier: "close", metadata: [metadata])
        connection.send(content: nil, contentContext: context, isComplete: true, completion: .contentProcessed { [connection] _ in connection.cancel() })
        queue.asyncAfter(deadline: .now() + 2) { [connection] in connection.cancel() }
    }
}
