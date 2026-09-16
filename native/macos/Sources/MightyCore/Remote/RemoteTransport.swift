import Foundation
import Darwin

public enum RemoteIPPolicy {
    public static func normalized(_ input: String) -> String { input.trimmingCharacters(in: CharacterSet(charactersIn: "[]")).lowercased() }
    public static func allowed(_ input: String, allowLoopback: Bool = false) -> Bool {
        let address = normalized(input)
        var ipv4 = in_addr()
        if inet_pton(AF_INET, address, &ipv4) == 1 {
            let bytes = withUnsafeBytes(of: &ipv4) { Array($0) }
            return bytes[0] == 100 && (64...127).contains(bytes[1]) || (allowLoopback && bytes[0] == 127)
        }
        var ipv6 = in6_addr()
        if inet_pton(AF_INET6, address, &ipv6) == 1 {
            let bytes = withUnsafeBytes(of: &ipv6) { Array($0) }
            if bytes[0..<6].elementsEqual([0xfd, 0x7a, 0x11, 0x5c, 0xa1, 0xe0]) { return true }
            if allowLoopback && bytes[0..<15].allSatisfy({ $0 == 0 }) && bytes[15] == 1 { return true }
            if bytes[0..<10].allSatisfy({ $0 == 0 }), bytes[10] == 255, bytes[11] == 255 {
                return bytes[12] == 100 && (64...127).contains(bytes[13]) || (allowLoopback && bytes[12] == 127)
            }
        }
        return false
    }
}

struct RemoteTarget: Sendable {
    let origin: String
    let host: String
    let port: Int
    let ip: String
}

public struct ParsedRemoteAddress: Sendable, Equatable {
    public let origin: String
    public let host: String
    public let port: Int
    public static func parse(_ value: String) throws -> ParsedRemoteAddress {
        guard value.count <= 2048, !value.contains("\0"), let url = URLComponents(string: value.trimmingCharacters(in: .whitespacesAndNewlines)), url.scheme == "http", let rawHost = url.host,
              url.user == nil, url.password == nil, url.query == nil, url.fragment == nil, ["", "/"].contains(url.path), let port = url.port, (1024...65535).contains(port) else {
            throw RemoteFailure("포트가 포함된 Tailscale HTTP 주소를 입력하세요. 예: http://100.x.x.x:43137")
        }
        let host = RemoteIPPolicy.normalized(rawHost)
        guard !host.isEmpty, !host.contains("%"), host.range(of: "^[a-z0-9.:-]+$", options: .regularExpression) != nil else { throw RemoteFailure("원격 호스트 이름이 올바르지 않습니다.") }
        return ParsedRemoteAddress(origin: "http://\(host.contains(":") ? "[\(host)]" : host):\(port)", host: host, port: port)
    }
}

private final class NoRedirectDelegate: NSObject, URLSessionTaskDelegate, @unchecked Sendable {
    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse, newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) { completionHandler(nil) }
}

enum RemoteTransport {
    static func resolve(_ address: ParsedRemoteAddress, peers: Set<String>, allowLoopback: Bool) async throws -> RemoteTarget {
        let ips: [String] = try await Task.detached(priority: .utility) {
            var hints = addrinfo()
            hints.ai_family = AF_UNSPEC; hints.ai_socktype = SOCK_STREAM; hints.ai_flags = AI_ADDRCONFIG
            var result: UnsafeMutablePointer<addrinfo>?
            guard getaddrinfo(address.host, nil, &hints, &result) == 0, let first = result else { throw RemoteFailure("원격 호스트 이름을 찾지 못했습니다.") }
            defer { freeaddrinfo(first) }
            var addresses: [String] = []
            var current: UnsafeMutablePointer<addrinfo>? = first
            while let entry = current, addresses.count < 32 {
                var buffer = [CChar](repeating: 0, count: Int(NI_MAXHOST))
                if getnameinfo(entry.pointee.ai_addr, entry.pointee.ai_addrlen, &buffer, socklen_t(buffer.count), nil, 0, NI_NUMERICHOST) == 0 { addresses.append(RemoteIPPolicy.normalized(String(cString: buffer))) }
                current = entry.pointee.ai_next
            }
            guard current == nil else { throw RemoteFailure("원격 호스트의 주소 응답이 너무 많습니다.") }
            return Array(Set(addresses))
        }.value
        guard !ips.isEmpty, ips.allSatisfy({ RemoteIPPolicy.allowed($0, allowLoopback: allowLoopback) }),
              allowLoopback || ips.allSatisfy({ peers.contains($0) }) else { throw RemoteFailure("현재 Tailscale에 연결된 기기의 주소만 사용할 수 있습니다.") }
        let ip = ips.sorted { !$0.contains(":") && $1.contains(":") }.first!
        return RemoteTarget(origin: address.origin, host: address.host, port: address.port, ip: ip)
    }

    static func request(_ target: RemoteTarget, token: String, method: String, path: String, body: Data? = nil, timeout: TimeInterval = 12) async throws -> Data {
        guard RemoteValidation.token(token), path.hasPrefix("/v1/"), !path.contains("\r"), !path.contains("\n") else { throw RemoteFailure("원격 요청이 올바르지 않습니다.") }
        let host = target.ip.contains(":") ? "[\(target.ip)]" : target.ip
        guard let url = URL(string: "http://\(host):\(target.port)\(path)") else { throw RemoteFailure("원격 주소가 올바르지 않습니다.") }
        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = timeout
        configuration.timeoutIntervalForResource = timeout
        configuration.waitsForConnectivity = false
        configuration.connectionProxyDictionary = ["HTTPEnable": 0, "HTTPSEnable": 0, "SOCKSEnable": 0]
        configuration.httpCookieStorage = nil; configuration.urlCredentialStorage = nil; configuration.urlCache = nil
        let session = URLSession(configuration: configuration, delegate: NoRedirectDelegate(), delegateQueue: nil)
        defer { session.invalidateAndCancel() }
        var request = URLRequest(url: url)
        request.httpMethod = method; request.httpBody = body
        request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        request.setValue("1", forHTTPHeaderField: "x-mighty-remote-version")
        request.setValue("1", forHTTPHeaderField: "x-mighty-activity")
        request.setValue("1", forHTTPHeaderField: "x-mighty-usage")
        request.setValue("1", forHTTPHeaderField: "x-mighty-graph")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        request.setValue("\(target.host.contains(":") ? "[\(target.host)]" : target.host):\(target.port)", forHTTPHeaderField: "Host")
        if body != nil { request.setValue("application/json", forHTTPHeaderField: "Content-Type") }
        let (bytes, response) = try await session.bytes(for: request)
        guard let response = response as? HTTPURLResponse, response.value(forHTTPHeaderField: "x-mighty-remote-version") == "1" else { throw RemoteFailure("호환되는 MightyClaude 서버 응답이 아닙니다.") }
        guard response.expectedContentLength <= 2 * 1024 * 1024 else { throw RemoteFailure("원격 응답 크기 제한을 초과했습니다.") }
        var data = Data()
        for try await byte in bytes {
            try Task.checkCancellation()
            data.append(byte)
            if data.count > 2 * 1024 * 1024 { throw RemoteFailure("원격 응답 크기 제한을 초과했습니다.") }
        }
        guard (200...299).contains(response.statusCode) else {
            let message = (try? JSONSerialization.jsonObject(with: data) as? [String: Any])?["error"] as? String
            throw RemoteFailure(String((message ?? "원격 요청 실패 (HTTP \(response.statusCode))").prefix(300)))
        }
        return data
    }
}
