import Foundation
import Network

public struct HTTPRequest: Sendable {
    public let method: String
    public let target: String
    public let headers: [String: String]
    public let body: Data
    public let remoteAddress: String
}

public struct HTTPResponse: Sendable {
    public var status: Int
    public var body: Data
    public var headers: [String: String]
    public init(status: Int, body: Data, headers: [String: String] = [:]) {
        self.status = status; self.body = body; self.headers = headers
    }
    public static func json(_ status: Int, _ object: [String: Any]) -> HTTPResponse {
        HTTPResponse(status: status, body: (try? JSONSerialization.data(withJSONObject: object)) ?? Data("{}".utf8))
    }
}

/// A bounded HTTP/1.1 endpoint. Each connection accepts one request and closes.
/// State is confined to `queue`; asynchronous handlers receive immutable data.
public final class HTTPServer: @unchecked Sendable {
    private final class Client: @unchecked Sendable {
        let connection: NWConnection
        var data = Data()
        var timer: DispatchWorkItem?
        var processing = false
        var bodyLimit = 512 * 1024
        var extendedTimeout = false
        init(_ connection: NWConnection) { self.connection = connection }
    }
    private let queue = DispatchQueue(label: "dev.mightyclaude.http")
    private let address: String
    private let port: UInt16
    private let handler: @Sendable (HTTPRequest) async -> HTTPResponse
    private let requestBodyLimit: (@Sendable (HTTPRequest) -> Int)?
    private var listener: NWListener?
    private var clients: [ObjectIdentifier: Client] = [:]
    private var continuation: CheckedContinuation<UInt16, Error>?
    private var closing = false
    private let headerLimit = 8_192
    private let bodyLimit = 512 * 1024

    public init(address: String, port: UInt16, requestBodyLimit: (@Sendable (HTTPRequest) -> Int)? = nil, handler: @escaping @Sendable (HTTPRequest) async -> HTTPResponse) {
        self.address = address; self.port = port; self.handler = handler; self.requestBodyLimit = requestBodyLimit
    }

    /// True while the listener is open; long-poll callers check it first.
    public var isListening: Bool { queue.sync { listener != nil && !closing } }

    public func start() async throws -> UInt16 {
        try await withCheckedThrowingContinuation { continuation in
            queue.async { [self] in
                guard !self.closing, self.listener == nil else {
                    continuation.resume(throwing: RemoteFailure("서버가 이미 시작되었거나 종료되었습니다.")); return
                }
                self.continuation = continuation
                do {
                    let parameters = NWParameters.tcp
                    parameters.allowLocalEndpointReuse = true
                    parameters.requiredLocalEndpoint = .hostPort(host: NWEndpoint.Host(self.address), port: NWEndpoint.Port(rawValue: self.port)!)
                    let listener = try NWListener(using: parameters)
                    self.listener = listener
                    listener.stateUpdateHandler = { [weak self] state in
                        guard let self else { return }
                        switch state {
                        case .ready:
                            guard let port = listener.port?.rawValue, !self.closing else { self.finishStart(.failure(RemoteFailure("서버 시작이 취소되었습니다."))); return }
                            self.finishStart(.success(port))
                        case .failed(let error): self.finishStart(.failure(error)); self.closeOnQueue()
                        case .cancelled: self.finishStart(.failure(RemoteFailure("서버 시작이 취소되었습니다.")))
                        default: break
                        }
                    }
                    listener.newConnectionHandler = { [weak self] connection in self?.accept(connection) }
                    listener.start(queue: self.queue)
                    self.queue.asyncAfter(deadline: .now() + 5) {
                        if self.continuation != nil { self.finishStart(.failure(RemoteFailure("서버 시작 시간이 초과되었습니다."))); self.closeOnQueue() }
                    }
                } catch { self.finishStart(.failure(error)) }
            }
        }
    }

    private func finishStart(_ result: Result<UInt16, Error>) {
        let pending = continuation; continuation = nil; pending?.resume(with: result)
    }

    private func accept(_ connection: NWConnection) {
        guard !closing, clients.count < 64 else { connection.cancel(); return }
        let client = Client(connection)
        clients[ObjectIdentifier(connection)] = client
        let timeout = DispatchWorkItem { [weak self, weak client] in if let client { self?.remove(client) } }
        client.timer = timeout
        queue.asyncAfter(deadline: .now() + 15, execute: timeout)
        connection.stateUpdateHandler = { [weak self, weak client] state in
            guard let client else { return }
            if case .failed = state { self?.remove(client) }
            if case .cancelled = state { self?.remove(client) }
        }
        connection.start(queue: queue)
        receive(client)
    }

    private func receive(_ client: Client) {
        client.connection.receive(minimumIncompleteLength: 1, maximumLength: 65_536) { [weak self, weak client] data, _, complete, error in
            guard let self, let client, !self.closing, self.clients[ObjectIdentifier(client.connection)] != nil else { return }
            if let data { client.data.append(data) }
            if client.data.count > client.bodyLimit + self.headerLimit { self.reply(client, .json(413, ["error": "요청 크기 제한을 초과했습니다."])); return }
            if self.parse(client) { return }
            if complete || error != nil { self.remove(client); return }
            self.receive(client)
        }
    }

    private func parse(_ client: Client) -> Bool {
        guard let separator = client.data.range(of: Data("\r\n\r\n".utf8)) else {
            if client.data.count > headerLimit { reply(client, .json(431, ["error": "헤더가 너무 큽니다."])); return true }
            return false
        }
        guard separator.lowerBound <= headerLimit,
              let header = String(data: client.data[..<separator.lowerBound], encoding: .utf8) else {
            reply(client, .json(400, ["error": "HTTP 헤더가 올바르지 않습니다."])); return true
        }
        let lines = header.components(separatedBy: "\r\n")
        let requestLine = (lines.first ?? "").split(separator: " ", omittingEmptySubsequences: false)
        guard requestLine.count == 3, ["GET", "POST"].contains(String(requestLine[0])), requestLine[2] == "HTTP/1.1", requestLine[1].hasPrefix("/"), lines.count <= 34 else {
            reply(client, .json(400, ["error": "HTTP 요청이 올바르지 않습니다."])); return true
        }
        var headers: [String: String] = [:]
        for line in lines.dropFirst() {
            guard let colon = line.firstIndex(of: ":"), !line.hasPrefix(" "), !line.hasPrefix("\t") else { reply(client, .json(400, ["error": "잘못된 헤더"])); return true }
            let key = String(line[..<colon]).lowercased()
            let value = line[line.index(after: colon)...].trimmingCharacters(in: .whitespaces)
            guard !key.isEmpty, key.range(of: "^[a-z0-9-]+$", options: .regularExpression) != nil, headers[key] == nil else { reply(client, .json(400, ["error": "중복 또는 잘못된 헤더"])); return true }
            headers[key] = value
        }
        guard headers["transfer-encoding"] == nil else { reply(client, .json(400, ["error": "Content-Length가 필요합니다."])); return true }
        let peer: String
        if case .hostPort(let host, _) = client.connection.endpoint { peer = String(describing: host) } else { peer = "" }
        let head = HTTPRequest(method: String(requestLine[0]), target: String(requestLine[1]), headers: headers, body: Data(), remoteAddress: peer)
        client.bodyLimit = min(AttachmentSupport.maximumRequestBytes, max(0, requestBodyLimit?(head) ?? bodyLimit))
        let lengthText = headers["content-length"] ?? "0"
        guard lengthText.range(of: "^[0-9]{1,9}$", options: .regularExpression) != nil, let length = Int(lengthText), length <= client.bodyLimit else {
            reply(client, .json(413, ["error": "요청 크기가 올바르지 않습니다."])); return true
        }
        if length > bodyLimit && !client.extendedTimeout {
            client.extendedTimeout = true; client.timer?.cancel()
            let timeout = DispatchWorkItem { [weak self, weak client] in if let client { self?.remove(client) } }
            client.timer = timeout; queue.asyncAfter(deadline: .now() + 60, execute: timeout)
        }
        let expected = separator.upperBound + length
        if client.data.count < expected { return false }
        guard client.data.count == expected else { reply(client, .json(400, ["error": "하나의 연결에는 하나의 요청만 허용합니다."])); return true }
        client.processing = true
        // The accept timer covered slow request delivery; a handler may now
        // long-poll, so give it its own generous deadline instead.
        client.timer?.cancel()
        let processing = DispatchWorkItem { [weak self, weak client] in if let client { self?.remove(client) } }
        client.timer = processing; queue.asyncAfter(deadline: .now() + 60, execute: processing)
        let request = HTTPRequest(method: String(requestLine[0]), target: String(requestLine[1]), headers: headers, body: Data(client.data[separator.upperBound..<expected]), remoteAddress: peer)
        client.data.removeAll(keepingCapacity: false)
        Task { [self, client] in
            let response = await handler(request)
            queue.async { [weak self, weak client] in if let client { self?.reply(client, response) } }
        }
        return true
    }

    private func reply(_ client: Client, _ response: HTTPResponse) {
        guard clients[ObjectIdentifier(client.connection)] != nil else { return }
        let reasons = [200: "OK", 202: "Accepted", 400: "Bad Request", 401: "Unauthorized", 403: "Forbidden", 404: "Not Found", 413: "Payload Too Large", 415: "Unsupported Media Type", 426: "Upgrade Required", 429: "Too Many Requests", 431: "Request Header Fields Too Large", 500: "Internal Server Error", 503: "Service Unavailable"]
        var header = "HTTP/1.1 \(response.status) \(reasons[response.status] ?? "Response")\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: \(response.body.count)\r\nConnection: close\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\n"
        for (key, value) in response.headers where !key.contains("\r") && !key.contains("\n") && !value.contains("\r") && !value.contains("\n") { header += "\(key): \(value)\r\n" }
        var payload = Data((header + "\r\n").utf8); payload.append(response.body)
        client.connection.send(content: payload, completion: .contentProcessed { [weak self, weak client] _ in if let client { self?.remove(client) } })
    }

    private func remove(_ client: Client) {
        client.timer?.cancel(); client.connection.stateUpdateHandler = nil
        clients.removeValue(forKey: ObjectIdentifier(client.connection))
        client.connection.cancel()
    }
    private func closeOnQueue() {
        closing = true
        finishStart(.failure(RemoteFailure("서버가 종료되었습니다.")))
        listener?.stateUpdateHandler = nil
        listener?.newConnectionHandler = nil
        listener?.cancel()
        for client in Array(clients.values) { remove(client) }
    }
    public func stop() async {
        await withCheckedContinuation { continuation in queue.async { self.closeOnQueue(); continuation.resume() } }
    }
}
