import Foundation

struct TailscaleProbe: Sendable {
    var state: TailscaleState
    var peers: Set<String>
}

enum TailscaleDiscovery {
    static func inspect() async -> TailscaleProbe {
        let search = (ProcessInfo.processInfo.environment["PATH"] ?? "").split(separator: ":").map(String.init)
        let paths = Array(Set(search + ["/opt/homebrew/bin", "/usr/local/bin", "/usr/bin"]))
        let candidates = paths.map { $0 + "/tailscale" } + ["/Applications/Tailscale.app/Contents/MacOS/Tailscale", "/Applications/Tailscale.app/Contents/MacOS/tailscale"]
        for candidate in candidates where FileManager.default.isExecutableFile(atPath: candidate) {
            do {
                let result = try await ProcessCapture.run(executable: URL(fileURLWithPath: candidate), arguments: ["status", "--json"], timeout: 5)
                guard result.exitCode == 0, let json = try JSONSerialization.jsonObject(with: result.stdout) as? [String: Any] else { continue }
                let own = json["Self"] as? [String: Any] ?? [:]
                let addresses = ((json["TailscaleIPs"] as? [String]) ?? (own["TailscaleIPs"] as? [String]) ?? []).map(RemoteIPPolicy.normalized).filter { RemoteIPPolicy.allowed($0) }
                var peers = Set(addresses)
                for row in (json["Peer"] as? [String: [String: Any]] ?? [:]).values {
                    for address in row["TailscaleIPs"] as? [String] ?? [] where RemoteIPPolicy.allowed(address) { peers.insert(RemoteIPPolicy.normalized(address)) }
                }
                let running = json["BackendState"] as? String == "Running" && !addresses.isEmpty
                let name = own["HostName"] as? String ?? own["DNSName"] as? String
                return TailscaleProbe(state: TailscaleState(available: running, addresses: addresses, deviceName: name, detail: running ? "Tailscale에 연결되어 있습니다." : "Tailscale을 실행하고 로그인해 주세요."), peers: peers)
            } catch { continue }
        }
        return TailscaleProbe(state: TailscaleState(detail: "Tailscale CLI를 찾거나 상태를 확인하지 못했습니다. Tailscale 설치·실행·로그인을 확인해 주세요."), peers: [])
    }
}
