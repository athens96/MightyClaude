import Foundation
import Darwin

/// One tool call forwarded by a per-pane MCP server to the app. Carries no pane
/// id: the pane is whatever the token resolves to.
public struct AgentIORequest: Codable, Sendable, Equatable {
    public var tool: String
    public var command: String?
    public var handle: String?
    public var url: String?
    public init(tool: String, command: String? = nil, handle: String? = nil, url: String? = nil) {
        self.tool = tool; self.command = command; self.handle = handle; self.url = url
    }
}

/// The wire frame: one JSON object per line, at most ``AgentIOWire/maxMessageBytes``.
/// It carries either a terminal and web tool call or a delegation tool call.
struct AgentIOEnvelope: Codable, Sendable {
    var token: String
    var request: AgentIORequest?
    var delegation: DelegationRequest?
}

/// The app's answer. `error` set means the call failed and nothing else is meaningful.
public struct AgentIOResponse: Codable, Sendable, Equatable {
    public var error: String?
    public var handle: String?
    public var status: String?
    public var exitCode: Int32?
    public var signal: Int32?
    public var output: String?
    public var outputDropped: Bool?
    public var moreRemains: Bool?
    public var destination: String?
    public var url: String?
    /// In-app was chosen but the in-app browser could not show the page, so
    /// `destination` is the system browser instead.
    public var inAppUnavailable: Bool?

    public init(error: String? = nil, handle: String? = nil, status: String? = nil, exitCode: Int32? = nil, signal: Int32? = nil, output: String? = nil, outputDropped: Bool? = nil, moreRemains: Bool? = nil, destination: String? = nil, url: String? = nil, inAppUnavailable: Bool? = nil) {
        self.error = error; self.handle = handle; self.status = status; self.exitCode = exitCode; self.signal = signal
        self.output = output; self.outputDropped = outputDropped; self.moreRemains = moreRemains; self.destination = destination; self.url = url
        self.inAppUnavailable = inAppUnavailable
    }

    public static func failure(_ message: String) -> AgentIOResponse { AgentIOResponse(error: message) }

    public init(_ result: TerminalRunResult) {
        self.init(handle: result.handle, status: result.status.rawValue, exitCode: result.exitCode, signal: result.signal, output: result.output, outputDropped: result.outputDropped, moreRemains: result.moreRemains)
    }
}

/// An answer the socket client can also make up itself when the call fails.
protocol AgentIOAnswer: Codable {
    static func failure(_ message: String) -> Self
}

extension AgentIOResponse: AgentIOAnswer {}
extension DelegationResponse: AgentIOAnswer {}

/// Limits shared by both ends of the agent IO socket.
public enum AgentIOWire {
    public static let maxMessageBytes = 1_048_576
    public static let maxCommandBytes = 65_536
    public static let maxHandleLength = 128

    /// Read one newline-terminated message from a blocking socket; nil on EOF,
    /// timeout, error, or a message over `maxMessageBytes`.
    static func readLine(fd: Int32) -> Data? {
        var message = Data(), chunk = [UInt8](repeating: 0, count: 16_384)
        while true {
            let count = Darwin.read(fd, &chunk, chunk.count)
            if count < 0, errno == EINTR { continue }
            guard count > 0 else { return nil }
            if let newline = chunk[0 ..< count].firstIndex(of: 10) {
                message.append(contentsOf: chunk[0 ..< newline])
                return message.count <= maxMessageBytes ? message : nil
            }
            message.append(contentsOf: chunk[0 ..< count])
            guard message.count <= maxMessageBytes else { return nil }
        }
    }

    static func writeAll(fd: Int32, _ data: Data) -> Bool {
        data.withUnsafeBytes { raw in
            guard let base = raw.baseAddress else { return true }
            var offset = 0
            while offset < raw.count {
                let count = Darwin.write(fd, base.advanced(by: offset), raw.count - offset)
                if count > 0 { offset += count } else if count < 0, errno == EINTR { continue } else { return false }
            }
            return true
        }
    }

    static func setTimeouts(fd: Int32, seconds: Int) {
        var timeout = timeval(tv_sec: seconds, tv_usec: 0)
        _ = setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &timeout, socklen_t(MemoryLayout<timeval>.size))
        _ = setsockopt(fd, SOL_SOCKET, SO_SNDTIMEO, &timeout, socklen_t(MemoryLayout<timeval>.size))
        var noSigPipe: Int32 = 1
        _ = setsockopt(fd, SOL_SOCKET, SO_NOSIGPIPE, &noSigPipe, socklen_t(MemoryLayout<Int32>.size))
    }

    /// Fill a `sockaddr_un` for `path`; nil when the path does not fit sun_path.
    static func address(_ path: String) -> sockaddr_un? {
        var address = sockaddr_un()
        let bytes = Array(path.utf8)
        guard !bytes.isEmpty, bytes.count < MemoryLayout.size(ofValue: address.sun_path) else { return nil }
        address.sun_family = sa_family_t(AF_UNIX)
        address.sun_len = UInt8(MemoryLayout<sockaddr_un>.size)
        withUnsafeMutableBytes(of: &address.sun_path) { raw in for (index, byte) in bytes.enumerated() { raw[index] = byte } }
        return address
    }

    /// The per-profile socket path: `<dataDirectory>/agent-io/io.sock`, or a
    /// short per-user directory under `$TMPDIR` when that exceeds sun_path.
    public static func socketPath(dataDirectory: URL, temporaryDirectory: String = NSTemporaryDirectory()) -> String {
        let preferred = dataDirectory.appendingPathComponent("agent-io", isDirectory: true).appendingPathComponent("io.sock").path
        if address(preferred) != nil { return preferred }
        // FNV-1a keeps one short, stable name per profile.
        var hash: UInt64 = 0xcbf29ce484222325
        for byte in dataDirectory.standardizedFileURL.path.utf8 { hash = (hash ^ UInt64(byte)) &* 0x100000001b3 }
        let root = URL(fileURLWithPath: temporaryDirectory, isDirectory: true).appendingPathComponent("mc-\(getuid())", isDirectory: true)
        return root.appendingPathComponent(String(format: "%08x.sock", UInt32(truncatingIfNeeded: hash))).path
    }
}

/// Serves one tool call, already resolved to the pane its token belongs to.
public protocol AgentIORequestHandler: Sendable {
    func handle(_ request: AgentIORequest, binding: PaneMCPBinding) async -> AgentIOResponse
}

/// App-side unix-socket server the per-pane MCP servers call back into.
///
/// Each connection carries one request line and gets one response line. The
/// token in the request is resolved through the shared ``PaneMCPBindingRegistry``;
/// an unknown or revoked token is rejected before a handler sees anything.
/// Terminal and web calls go to `handler`, delegation calls to `delegation`.
/// The socket lives in a 0700 directory, is 0600 itself, and only accepts
/// peers running as the same user.
public final class AgentIOSocketServer: @unchecked Sendable {
    public static let unknownTokenMessage = "This agent pane is no longer connected to Mighty Claude. Reopen the pane and try again."
    static let malformedMessage = "The request to Mighty Claude was malformed."

    public let socketPath: String
    private let bindings: PaneMCPBindingRegistry
    private let handler: any AgentIORequestHandler
    private let delegation: (any DelegationRequestHandler)?
    private let lock = NSLock()
    private var listenFD: Int32 = -1
    private var stopped = false
    private var acceptThread: Thread?

    public init(socketPath: String, bindings: PaneMCPBindingRegistry, handler: any AgentIORequestHandler, delegation: (any DelegationRequestHandler)? = nil) {
        self.socketPath = socketPath; self.bindings = bindings; self.handler = handler; self.delegation = delegation
    }

    public func start() throws {
        guard var address = AgentIOWire.address(socketPath) else { throw MightyError("agent IO socket path does not fit sun_path") }
        let directory = (socketPath as NSString).deletingLastPathComponent
        try FileManager.default.createDirectory(atPath: directory, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        guard chmod(directory, 0o700) == 0 else { throw MightyError("could not restrict the agent IO socket folder") }
        unlink(socketPath)
        let fd = socket(AF_UNIX, SOCK_STREAM, 0)
        guard fd >= 0 else { throw MightyError("could not create the agent IO socket") }
        _ = fcntl(fd, F_SETFD, FD_CLOEXEC)
        let bound = withUnsafePointer(to: &address) { $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { bind(fd, $0, socklen_t(MemoryLayout<sockaddr_un>.size)) } }
        guard bound == 0, chmod(socketPath, 0o600) == 0, listen(fd, 16) == 0 else {
            Darwin.close(fd); unlink(socketPath)
            throw MightyError("could not listen on the agent IO socket: \(String(cString: strerror(errno)))")
        }
        lock.lock(); listenFD = fd; stopped = false; lock.unlock()
        let thread = Thread { [weak self] in self?.acceptLoop(fd: fd) }
        thread.name = "dev.mightyclaude.agent-io"
        acceptThread = thread
        thread.start()
    }

    /// Stop accepting and remove the socket file.
    public func stop() {
        lock.lock(); let fd = listenFD; listenFD = -1; stopped = true; lock.unlock()
        if fd >= 0 { Darwin.close(fd) }
        unlink(socketPath)
    }

    private var isStopped: Bool { lock.lock(); defer { lock.unlock() }; return stopped }

    private func acceptLoop(fd: Int32) {
        while !isStopped {
            var descriptor = pollfd(fd: fd, events: Int16(POLLIN), revents: 0)
            let ready = poll(&descriptor, 1, 200)
            if ready < 0, errno != EINTR { return }
            guard ready > 0, !isStopped else { continue }
            let client = accept(fd, nil, nil)
            guard client >= 0 else { if errno == EINTR || errno == EAGAIN || errno == ECONNABORTED { continue }; return }
            _ = fcntl(client, F_SETFD, FD_CLOEXEC)
            var uid: uid_t = 0, gid: gid_t = 0
            guard getpeereid(client, &uid, &gid) == 0, uid == getuid() else { Darwin.close(client); continue }
            AgentIOWire.setTimeouts(fd: client, seconds: 10)
            DispatchQueue.global(qos: .userInitiated).async { [self] in serve(client: client) }
        }
    }

    private func serve(client: Int32) {
        guard let line = AgentIOWire.readLine(fd: client) else { Darwin.close(client); return }
        let envelope = try? JSONDecoder().decode(AgentIOEnvelope.self, from: line)
        Task { [self] in
            let encoded: Data?
            if let envelope, let call = envelope.delegation {
                encoded = try? JSONEncoder().encode(await answer(call, token: envelope.token))
            } else {
                let response: AgentIOResponse
                if let envelope, let request = envelope.request {
                    if let binding = bindings.binding(forToken: envelope.token) {
                        response = await handler.handle(request, binding: binding)
                    } else { response = .failure(Self.unknownTokenMessage) }
                } else { response = .failure(Self.malformedMessage) }
                encoded = try? JSONEncoder().encode(response)
            }
            var data = encoded ?? Data("{\"error\":\"encoding failed\"}".utf8)
            data.append(10)
            _ = AgentIOWire.writeAll(fd: client, data)
            Darwin.close(client)
        }
    }

    /// A delegation call, from whatever pane its token belongs to. Without a
    /// delegation handler the server is not attached, so the call reaches nothing.
    private func answer(_ call: DelegationRequest, token: String) async -> DelegationResponse {
        guard let binding = bindings.binding(forToken: token) else { return .failure(Self.unknownTokenMessage) }
        guard let delegation else { return .failure(DelegationIOHandler.detachedMessage) }
        return await delegation.handle(call, binding: binding)
    }
}

/// Sends one request to the app over the agent IO socket and waits for the answer.
public enum AgentIOSocketClient {
    /// Long enough for a 12 s run wait, a 10 s stop or the 30 s URL dialog.
    public static let responseTimeoutSeconds = 60

    public static func send(_ request: AgentIORequest, token: String, socketPath: String) -> AgentIOResponse {
        exchange(AgentIOEnvelope(token: token, request: request), socketPath: socketPath)
    }

    /// One delegation call: the same socket, token and 60 s limit as the terminal tools.
    public static func send(_ request: DelegationRequest, token: String, socketPath: String) -> DelegationResponse {
        exchange(AgentIOEnvelope(token: token, delegation: request), socketPath: socketPath)
    }

    private static func exchange<Answer: AgentIOAnswer>(_ envelope: AgentIOEnvelope, socketPath: String) -> Answer {
        guard var address = AgentIOWire.address(socketPath) else { return .failure("Mighty Claude's agent socket path is invalid.") }
        let fd = socket(AF_UNIX, SOCK_STREAM, 0)
        guard fd >= 0 else { return .failure("Could not open a connection to Mighty Claude.") }
        defer { Darwin.close(fd) }
        AgentIOWire.setTimeouts(fd: fd, seconds: responseTimeoutSeconds)
        let connected = withUnsafePointer(to: &address) { $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { connect(fd, $0, socklen_t(MemoryLayout<sockaddr_un>.size)) } }
        guard connected == 0 else { return .failure("Mighty Claude is not running or not accepting agent tool calls.") }
        guard var data = try? JSONEncoder().encode(envelope), data.count < AgentIOWire.maxMessageBytes else { return .failure("The request is too large.") }
        data.append(10)
        guard AgentIOWire.writeAll(fd: fd, data) else { return .failure("Could not send the request to Mighty Claude.") }
        guard let line = AgentIOWire.readLine(fd: fd), let response = try? JSONDecoder().decode(Answer.self, from: line) else { return .failure("Mighty Claude did not answer the request.") }
        return response
    }
}
