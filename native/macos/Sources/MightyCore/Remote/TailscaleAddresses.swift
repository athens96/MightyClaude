import Foundation
import Darwin

/// This computer's Tailscale addresses, read from its network interfaces
/// (`getifaddrs`) without the Tailscale CLI. Tailscale gives every device an IPv4
/// address in the CGNAT range 100.64.0.0/10 and an IPv6 address in
/// fd7a:115c:a1e0::/48; nothing else is one.
public enum TailscaleAddresses {
    /// Whether `address` (an IPv4 or IPv6 literal, optionally in brackets) is in
    /// one of Tailscale's two ranges. An IPv4-mapped IPv6 address counts as its IPv4.
    public static func contains(_ address: String) -> Bool {
        let value = address.trimmingCharacters(in: CharacterSet(charactersIn: "[]")).lowercased()
        var ipv4 = in_addr()
        if inet_pton(AF_INET, value, &ipv4) == 1 {
            let bytes = withUnsafeBytes(of: &ipv4) { Array($0) }
            return bytes[0] == 100 && (64...127).contains(bytes[1])
        }
        var ipv6 = in6_addr()
        guard !value.contains("%"), inet_pton(AF_INET6, value, &ipv6) == 1 else { return false }
        let bytes = withUnsafeBytes(of: &ipv6) { Array($0) }
        if bytes[0..<6].elementsEqual([0xfd, 0x7a, 0x11, 0x5c, 0xa1, 0xe0]) { return true }
        if bytes[0..<10].allSatisfy({ $0 == 0 }), bytes[10] == 0xff, bytes[11] == 0xff {
            return bytes[12] == 100 && (64...127).contains(bytes[13])
        }
        return false
    }

    /// The Tailscale addresses among `candidates` (every interface's addresses,
    /// in the order read): each once, in its canonical spelling, IPv4 first.
    /// Pure, so the filter is tested without touching the machine's interfaces.
    public static func select(_ candidates: [String]) -> [String] {
        var seen = Set<String>(); var ipv4: [String] = []; var ipv6: [String] = []
        for candidate in candidates where contains(candidate) {
            let value = canonical(candidate)
            guard seen.insert(value).inserted else { continue }
            if value.contains(":") { ipv6.append(value) } else { ipv4.append(value) }
        }
        return ipv4 + ipv6
    }

    /// The Tailscale addresses among `interfaces` (name and address, as read):
    /// only a tunnel interface's (`utun…`) count. A carrier-grade NAT network
    /// can hand Wi-Fi or Ethernet an address in 100.64.0.0/10 too, and that one
    /// is outside WireGuard. Pure, like `select(_:)`.
    public static func select(interfaces: [(name: String, address: String)]) -> [String] {
        select(interfaces.filter { $0.name.hasPrefix("utun") }.map(\.address))
    }

    /// The addresses this computer has on Tailscale now; empty while Tailscale is off.
    public static func current() -> [String] {
        var head: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&head) == 0, let first = head else { return [] }
        defer { freeifaddrs(first) }
        var found: [(name: String, address: String)] = []
        var cursor: UnsafeMutablePointer<ifaddrs>? = first
        while let entry = cursor {
            defer { cursor = entry.pointee.ifa_next }
            guard let address = entry.pointee.ifa_addr, entry.pointee.ifa_flags & UInt32(IFF_UP) != 0 else { continue }
            let family = Int32(address.pointee.sa_family)
            guard family == AF_INET || family == AF_INET6 else { continue }
            var buffer = [CChar](repeating: 0, count: Int(NI_MAXHOST))
            let length = socklen_t(family == AF_INET ? MemoryLayout<sockaddr_in>.size : MemoryLayout<sockaddr_in6>.size)
            if getnameinfo(address, length, &buffer, socklen_t(buffer.count), nil, 0, NI_NUMERICHOST) == 0 {
                found.append((String(cString: entry.pointee.ifa_name), String(cString: buffer)))
            }
        }
        return select(interfaces: found)
    }

    /// The local address this computer would send from to reach `ip`, as its
    /// routing table decides (a UDP socket is connected, nothing is sent).
    /// A peer is reached over Tailscale only when that is a Tailscale address.
    public static func sourceAddress(toward ip: String) -> String? {
        var hints = addrinfo(); hints.ai_flags = AI_NUMERICHOST; hints.ai_socktype = SOCK_DGRAM; hints.ai_family = AF_UNSPEC
        var result: UnsafeMutablePointer<addrinfo>?
        guard getaddrinfo(ip, "9", &hints, &result) == 0, let target = result else { return nil }
        defer { freeaddrinfo(target) }
        let socketFD = socket(target.pointee.ai_family, SOCK_DGRAM, 0)
        guard socketFD >= 0 else { return nil }
        defer { close(socketFD) }
        guard connect(socketFD, target.pointee.ai_addr, target.pointee.ai_addrlen) == 0 else { return nil }
        var local = sockaddr_storage(); var length = socklen_t(MemoryLayout<sockaddr_storage>.size)
        let named = withUnsafeMutablePointer(to: &local) { $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { getsockname(socketFD, $0, &length) } }
        guard named == 0 else { return nil }
        var buffer = [CChar](repeating: 0, count: Int(NI_MAXHOST))
        let printed = withUnsafePointer(to: &local) { $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { getnameinfo($0, length, &buffer, socklen_t(buffer.count), nil, 0, NI_NUMERICHOST) } }
        guard printed == 0 else { return nil }
        let text = String(cString: buffer)
        return canonical(text.split(separator: "%", maxSplits: 1).first.map(String.init) ?? text)
    }

    /// One spelling per address, so the same address read twice is one entry.
    static func canonical(_ address: String) -> String {
        let value = address.trimmingCharacters(in: CharacterSet(charactersIn: "[]")).lowercased()
        var ipv6 = in6_addr()
        guard value.contains(":"), inet_pton(AF_INET6, value, &ipv6) == 1 else { return value }
        var buffer = [CChar](repeating: 0, count: Int(INET6_ADDRSTRLEN))
        return inet_ntop(AF_INET6, &ipv6, &buffer, socklen_t(buffer.count)).map { String(cString: $0) } ?? value
    }
}
