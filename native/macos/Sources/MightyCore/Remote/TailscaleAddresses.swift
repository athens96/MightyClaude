import Foundation
import Darwin
import Network

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

    /// The Tailscale addresses among `interfaces` (name and address, as read).
    /// Only a tunnel interface (`utun…`) that also carries an address in
    /// Tailscale's own IPv6 range fd7a:115c:a1e0::/48 is Tailscale's; that
    /// interface's addresses in both ranges count. A carrier-grade NAT network
    /// can hand Wi-Fi or Ethernet an address in 100.64.0.0/10, and other VPNs
    /// (Cloudflare WARP's 100.96.0.0/12, for one) put one on their own tunnel.
    /// Pure, so the rule is tested without touching the machine's interfaces.
    public static func select(interfaces: [(name: String, address: String)]) -> [String] {
        owned(interfaces: interfaces).map(\.address)
    }

    /// `select(interfaces:)` with each address's interface: each address once, in
    /// its canonical spelling (a `%scope` dropped), IPv4 first, each family sorted.
    public static func owned(interfaces: [(name: String, address: String)]) -> [(name: String, address: String)] {
        let read = interfaces.map { (name: $0.name, address: unscoped($0.address)) }
        let tailnet = Set(read.filter { $0.name.hasPrefix("utun") && inTailscaleIPv6Range($0.address) }.map(\.name))
        var seen = Set<String>()
        let found = read.filter { tailnet.contains($0.name) && contains($0.address) }
            .map { (name: $0.name, address: canonical($0.address)) }
            .filter { seen.insert($0.address).inserted }
        let ipv4 = found.filter { !$0.address.contains(":") }.sorted { $0.address < $1.address }
        let ipv6 = found.filter { $0.address.contains(":") }.sorted { $0.address < $1.address }
        return ipv4 + ipv6
    }

    /// fd7a:115c:a1e0::/48, which only Tailscale hands out.
    static func inTailscaleIPv6Range(_ address: String) -> Bool {
        var ipv6 = in6_addr()
        let value = address.trimmingCharacters(in: CharacterSet(charactersIn: "[]")).lowercased()
        guard !value.contains("%"), inet_pton(AF_INET6, value, &ipv6) == 1 else { return false }
        return withUnsafeBytes(of: &ipv6) { Array($0) }[0..<6].elementsEqual([0xfd, 0x7a, 0x11, 0x5c, 0xa1, 0xe0])
    }

    /// The addresses this computer has on Tailscale now; empty while Tailscale is off.
    public static func current() -> [String] { select(interfaces: interfaceAddresses()) }

    /// The same with each address's interface, the name a listener is pinned to.
    static func currentByInterface() -> [(name: String, address: String)] { owned(interfaces: interfaceAddresses()) }

    /// The tunnel interface that carries `address` among this computer's Tailscale addresses.
    public static func interface(owning address: String) -> String? {
        let value = canonical(address)
        return owned(interfaces: interfaceAddresses()).first { $0.address == value }?.name
    }

    /// Every up interface's IPv4 and IPv6 addresses, as `getifaddrs` reads them.
    static func interfaceAddresses() -> [(name: String, address: String)] {
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
        return found
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
        return canonical(unscoped(text))
    }

    /// The address without its `%interface` scope.
    static func unscoped(_ address: String) -> String {
        address.split(separator: "%", maxSplits: 1).first.map(String.init) ?? address
    }

    /// The interfaces Network.framework lists under these names, read from a path
    /// monitor of the "other" type (where tunnels are). A name it does not list
    /// within a second is missing from the result.
    static func tunnelInterfaces(named names: Set<String>) async -> [String: NWInterface] {
        guard !names.isEmpty else { return [:] }
        let monitor = NWPathMonitor(requiredInterfaceType: .other)
        let queue = DispatchQueue(label: "dev.mightyclaude.tailscale.interface")
        let once = OnceFlag()
        let found = await withCheckedContinuation { (continuation: CheckedContinuation<[String: NWInterface], Never>) in
            monitor.pathUpdateHandler = { path in
                guard once.claim() else { return }
                var result: [String: NWInterface] = [:]
                for interface in path.availableInterfaces where names.contains(interface.name) { result[interface.name] = interface }
                continuation.resume(returning: result)
            }
            monitor.start(queue: queue)
            queue.asyncAfter(deadline: .now() + 1) { if once.claim() { continuation.resume(returning: [:]) } }
        }
        monitor.cancel()
        return found
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
