import Foundation
import Darwin

/// Which peers the remote-workspace host answers and the client connects to:
/// Tailscale addresses only. Loopback is allowed only where a test injects
/// `allowLoopback` in code; no setting, connection field or IPC reaches it.
public enum RemotePeerPolicy {
    public static func allowed(_ address: String, allowLoopback: Bool = false) -> Bool {
        // A peer's address as the network stack reports it may carry its
        // interface (`fd7a:…%utun4`); the address alone decides.
        let unscoped = address.split(separator: "%", maxSplits: 1).first.map(String.init) ?? address
        if TailscaleAddresses.contains(unscoped) { return true }
        guard allowLoopback else { return false }
        let value = unscoped.trimmingCharacters(in: CharacterSet(charactersIn: "[]")).lowercased()
        var ipv4 = in_addr()
        if inet_pton(AF_INET, value, &ipv4) == 1 { return withUnsafeBytes(of: &ipv4) { $0[0] } == 127 }
        return value == "::1"
    }
}

/// A host address as the user typed it: a Tailscale IP or a MagicDNS name, with
/// an optional port (43137 when left out) and an optional `http://`.
public struct RemoteAddress: Sendable, Equatable {
    public let host: String
    public let port: Int
    /// `host:port` (IPv6 in brackets): what is saved and shown.
    public var display: String { (host.contains(":") ? "[\(host)]" : host) + ":\(port)" }

    public static func parse(_ input: String) throws -> RemoteAddress {
        var text = input.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty, text.count <= 300, !text.contains("\0") else { throw MightyError(L("remote.connect.error.badAddress")) }
        if text.lowercased().hasPrefix("http://") { text.removeFirst(7) }
        if text.hasSuffix("/") { text.removeLast() }
        guard !text.contains("/"), !text.contains("@"), !text.contains("?"), !text.contains("#"), !text.contains("%"),
              !text.contains("://") else { throw MightyError(L("remote.connect.error.badAddress")) }
        var host = text; var port = RemoteProtocol.defaultPort
        if text.hasPrefix("[") {
            guard let close = text.firstIndex(of: "]") else { throw MightyError(L("remote.connect.error.badAddress")) }
            host = String(text[text.index(after: text.startIndex)..<close])
            let rest = text[text.index(after: close)...]
            if !rest.isEmpty {
                guard rest.hasPrefix(":"), let value = Int(rest.dropFirst()) else { throw MightyError(L("remote.connect.error.badAddress")) }
                port = value
            }
        } else if text.filter({ $0 == ":" }).count == 1, let colon = text.firstIndex(of: ":") {
            host = String(text[..<colon])
            guard let value = Int(text[text.index(after: colon)...]) else { throw MightyError(L("remote.connect.error.badAddress")) }
            port = value
        }
        host = host.lowercased()
        guard (1024...65535).contains(port) else { throw MightyError(L("remote.connect.error.badPort")) }
        guard !host.isEmpty, host.count <= 253, host.range(of: "^[a-z0-9.:-]+$", options: .regularExpression) != nil,
              !host.hasPrefix("-"), !host.hasPrefix("."), !host.contains("..") else { throw MightyError(L("remote.connect.error.badAddress")) }
        // A colon is only ever an IPv6 literal's.
        if host.contains(":") {
            var ipv6 = in6_addr()
            guard inet_pton(AF_INET6, host, &ipv6) == 1 else { throw MightyError(L("remote.connect.error.badAddress")) }
            host = TailscaleAddresses.canonical(host)
        }
        return RemoteAddress(host: host, port: port)
    }
}

/// Where a connection's requests go: the address checked once, at connect or refresh.
struct RemoteTarget: Sendable, Equatable {
    let address: RemoteAddress
    /// The resolved IP every request goes to; never resolved again per request.
    let ip: String
    let port: Int
}

private final class RefuseRedirects: NSObject, URLSessionTaskDelegate, @unchecked Sendable {
    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse, newRequest request: URLRequest,
                    completionHandler: @escaping (URLRequest?) -> Void) { completionHandler(nil) }
}

enum RemoteTransport {
    /// Resolves `address` (a MagicDNS name through the system resolver) and
    /// accepts it only when every address it names is a Tailscale one.
    static func resolve(_ address: RemoteAddress, allowLoopback: Bool) async throws -> RemoteTarget {
        let host = address.host
        let ips: [String] = try await Task.detached(priority: .utility) {
            var hints = addrinfo()
            hints.ai_family = AF_UNSPEC; hints.ai_socktype = SOCK_STREAM
            var result: UnsafeMutablePointer<addrinfo>?
            guard getaddrinfo(host, nil, &hints, &result) == 0, let first = result else { throw MightyError(L("remote.connect.error.unresolved")) }
            defer { freeaddrinfo(first) }
            var found: [String] = []
            var current: UnsafeMutablePointer<addrinfo>? = first
            while let entry = current, found.count < 32 {
                var buffer = [CChar](repeating: 0, count: Int(NI_MAXHOST))
                if getnameinfo(entry.pointee.ai_addr, entry.pointee.ai_addrlen, &buffer, socklen_t(buffer.count), nil, 0, NI_NUMERICHOST) == 0 {
                    found.append(TailscaleAddresses.canonical(String(cString: buffer)))
                }
                current = entry.pointee.ai_next
            }
            return Array(Set(found))
        }.value
        guard !ips.isEmpty, ips.allSatisfy({ RemotePeerPolicy.allowed($0, allowLoopback: allowLoopback) }) else {
            throw MightyError(L("remote.connect.error.notTailscale"))
        }
        let ip = ips.sorted { (!$0.contains(":") && $1.contains(":")) || ($0.contains(":") == $1.contains(":") && $0 < $1) }[0]
        return RemoteTarget(address: address, ip: ip, port: address.port)
    }

    /// One authenticated request. The key rides in the Authorization header and
    /// a prompt in the body, never in the URL; a redirect is answered, never
    /// followed; the answer is read up to `maximumBytes`.
    static func request(_ target: RemoteTarget, key: String, method: String, path: String, body: Data? = nil,
                        timeout: TimeInterval = RemoteProtocol.requestTimeout, maximumBytes: Int = RemoteProtocol.maximumResponseBytes) async throws -> Data {
        guard RemoteWorkspaceValidation.key(key), path.hasPrefix(RemoteProtocol.pathPrefix), path.range(of: "^[A-Za-z0-9/._:?=-]+$", options: .regularExpression) != nil else {
            throw MightyError(L("remote.connect.error.badRequest"))
        }
        let host = target.ip.contains(":") ? "[\(target.ip)]" : target.ip
        guard let url = URL(string: "http://\(host):\(target.port)\(path)") else { throw MightyError(L("remote.connect.error.badRequest")) }
        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = timeout
        configuration.timeoutIntervalForResource = timeout
        configuration.waitsForConnectivity = false
        // Straight to the peer: a system proxy would see the key.
        configuration.connectionProxyDictionary = ["HTTPEnable": 0, "HTTPSEnable": 0, "SOCKSEnable": 0]
        configuration.httpCookieStorage = nil; configuration.urlCredentialStorage = nil; configuration.urlCache = nil
        configuration.httpShouldSetCookies = false
        let session = URLSession(configuration: configuration, delegate: RefuseRedirects(), delegateQueue: nil)
        defer { session.invalidateAndCancel() }
        var request = URLRequest(url: url)
        request.httpMethod = method; request.httpBody = body
        request.setValue("Bearer \(key)", forHTTPHeaderField: "Authorization")
        request.setValue("\(RemoteProtocol.version)", forHTTPHeaderField: RemoteProtocol.header)
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        if body != nil { request.setValue("application/json", forHTTPHeaderField: "Content-Type") }
        let (bytes, response) = try await session.bytes(for: request)
        guard let response = response as? HTTPURLResponse, response.value(forHTTPHeaderField: RemoteProtocol.header) == "\(RemoteProtocol.version)" else {
            throw MightyError(L("remote.connect.error.notMighty"))
        }
        guard (200...299).contains(response.statusCode) || (400...599).contains(response.statusCode) else { throw MightyError(L("remote.connect.error.unexpectedStatus", ["status": "\(response.statusCode)"])) }
        guard response.expectedContentLength <= Int64(maximumBytes) else { throw MightyError(L("remote.connect.error.responseTooLarge")) }
        var data = Data()
        for try await byte in bytes {
            data.append(byte)
            if data.count > maximumBytes { throw MightyError(L("remote.connect.error.responseTooLarge")) }
        }
        guard (200...299).contains(response.statusCode) else {
            let message = (try? JSONSerialization.jsonObject(with: data) as? [String: Any])?["error"] as? String
            throw MightyError(String((message ?? L("remote.connect.error.unexpectedStatus", ["status": "\(response.statusCode)"])).prefix(300)))
        }
        return data
    }
}
