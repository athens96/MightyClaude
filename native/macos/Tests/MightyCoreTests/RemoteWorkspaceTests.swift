import Testing
import Foundation
import Darwin
@testable import MightyCore

/// Tailscale remote workspaces (docs/remote-workspaces.md): address filtering,
/// the key and its checks, request limits, redirects, saved state, and host and
/// client talking over loopback (injected in code, as only tests may).
struct RemoteWorkspaceAddressTests {
    @Test func onlyTailscaleRangesCount() {
        for address in ["100.64.0.0", "100.64.0.1", "100.100.100.100", "100.101.102.103", "100.127.255.255", "fd7a:115c:a1e0::1", "fd7a:115c:a1e0:ffff:ffff:ffff:ffff:ffff", "[fd7a:115c:a1e0:ab12:4843:cd96:6258:b240]", "FD7A:115C:A1E0::9", "::ffff:100.100.1.1"] {
            #expect(TailscaleAddresses.contains(address), "\(address)")
        }
        for address in ["100.63.255.255", "100.128.0.0", "100.128.0.1", "10.0.0.1", "192.168.0.5", "192.168.1.2", "127.0.0.1", "0.0.0.0", "::1", "::", "fd7a:115c:a1e1::1", "fd7a:115c:a1df:ffff::1", "fe80::1", "fe80::1%utun4", "fd7a:115c:a1e0::1%utun4", "my-mac", "", "100.64.0"] {
            #expect(!TailscaleAddresses.contains(address), "\(address)")
        }
    }

    @Test func selectKeepsEachTailscaleAddressOnceWithIPv4First() {
        let read: [(name: String, address: String)] = [("utun4", "fe80::1"), ("utun4", "fd7a:115c:a1e0:0::5"), ("utun4", "100.101.1.2"), ("utun4", "192.168.1.3"), ("utun4", "FD7A:115C:A1E0::5"),
                                                       ("utun4", "100.101.1.2"), ("utun4", "127.0.0.1"), ("utun4", "100.64.9.9")]
        #expect(TailscaleAddresses.select(interfaces: read) == ["100.101.1.2", "100.64.9.9", "fd7a:115c:a1e0::5"])
        #expect(TailscaleAddresses.select(interfaces: []).isEmpty)
        // Only a tunnel's address counts: carrier-grade NAT can put 100.64/10 on Wi-Fi too.
        let interfaces: [(name: String, address: String)] = [("en0", "100.72.1.9"), ("utun4", "100.101.1.2"), ("utun4", "fd7a:115c:a1e0::5"), ("lo0", "127.0.0.1"), ("en0", "fd7a:115c:a1e0::9")]
        #expect(TailscaleAddresses.select(interfaces: interfaces) == ["100.101.1.2", "fd7a:115c:a1e0::5"])
        // A tunnel counts only with an address in Tailscale's own IPv6 range: a
        // WARP-like utun with only 100.96/12 (and link-local IPv6) is another VPN.
        let warp: [(name: String, address: String)] = [("utun3", "100.96.0.2"), ("utun3", "fe80::1"), ("utun7", "100.80.4.4")]
        #expect(TailscaleAddresses.select(interfaces: warp).isEmpty)
        #expect(TailscaleAddresses.select(interfaces: warp + interfaces) == ["100.101.1.2", "fd7a:115c:a1e0::5"])
        #expect(TailscaleAddresses.owned(interfaces: warp + interfaces).map(\.name) == ["utun4", "utun4"])
        // Both ranges of the Tailscale tunnel count, IPv4 found beside the IPv6.
        #expect(TailscaleAddresses.select(interfaces: [("utun9", "fd7a:115c:a1e0::7"), ("utun9", "100.70.0.7")]) == ["100.70.0.7", "fd7a:115c:a1e0::7"])
        // The route to a peer is read without sending anything.
        #expect(TailscaleAddresses.sourceAddress(toward: "127.0.0.1") == "127.0.0.1")
        // Whatever this machine has, only Tailscale addresses come back.
        #expect(TailscaleAddresses.current().allSatisfy(TailscaleAddresses.contains))
    }

    @Test func onlyATunnelCarryingTailscalesIPv6PrefixCountsAndOnlyItsAddressesAreKept() {
        let interfaces: [(name: String, address: String)] = [
            ("lo0", "127.0.0.1"),
            ("lo0", "::1"),
            ("en0", "192.168.0.5"),
            ("en0", "fe80::1c2b:3d4e:5f60:7182%en0"),
            // A hotspot handing out CGNAT addresses on Wi-Fi is not Tailscale.
            ("en0", "100.70.1.2"),
            // WARP and other VPNs put 100.64/10 on a tunnel too, but not fd7a:115c:a1e0::/48.
            ("utun2", "100.96.0.2"),
            ("utun2", "fd00:abcd::2"),
            ("utun3", "10.8.0.2"),
            // Tailscale: its tunnel carries both ranges.
            ("utun4", "fe80::abcd%utun4"),
            ("utun4", "fd7a:115c:a1e0::1234:5678"),
            ("utun4", "100.101.102.103"),
            ("utun4", "100.128.0.1"),
            ("utun4", "10.0.0.9"),
        ]
        #expect(TailscaleAddresses.select(interfaces: interfaces) == ["100.101.102.103", "fd7a:115c:a1e0::1234:5678"])
        #expect(TailscaleAddresses.owned(interfaces: interfaces).map(\.name) == ["utun4", "utun4"])
        // A tunnel with the CGNAT address alone is never taken, however it is named.
        #expect(TailscaleAddresses.select(interfaces: [("utun7", "100.101.102.103")]).isEmpty)
        // The IPv6 prefix alone is enough to recognise the tunnel.
        #expect(TailscaleAddresses.select(interfaces: [("utun7", "fd7a:115c:a1e0::9")]) == ["fd7a:115c:a1e0::9"])
        // The prefix on a non-tunnel interface does not count.
        #expect(TailscaleAddresses.select(interfaces: [("en0", "fd7a:115c:a1e0::9"), ("en0", "100.101.102.103")]).isEmpty)
        // An address read with its %scope is taken without it.
        #expect(TailscaleAddresses.select(interfaces: [("utun7", "fd7a:115c:a1e0::9%utun7"), ("utun7", "100.101.102.103")]) == ["100.101.102.103", "fd7a:115c:a1e0::9"])
        // Each family is sorted, whatever order the interfaces were read in.
        #expect(TailscaleAddresses.select(interfaces: [("utun7", "fd7a:115c:a1e0::b"), ("utun7", "100.101.1.2"), ("utun7", "fd7a:115c:a1e0::a"), ("utun7", "100.100.1.2")])
                == ["100.100.1.2", "100.101.1.2", "fd7a:115c:a1e0::a", "fd7a:115c:a1e0::b"])
        // Whatever this machine has, detection never offers loopback or a LAN address.
        for entry in TailscaleAddresses.currentByInterface() {
            #expect(entry.name.hasPrefix("utun"))
            #expect(TailscaleAddresses.contains(entry.address))
        }
        // A peer is on the tailnet by the same rule.
        #expect(TailscaleAddresses.contains("100.64.0.1") && TailscaleAddresses.contains("fd7a:115c:a1e0::1"))
        #expect(!TailscaleAddresses.contains("192.168.0.5") && !TailscaleAddresses.contains("127.0.0.1"))
    }

    @Test func loopbackIsAllowedOnlyWhenInjected() {
        #expect(!RemotePeerPolicy.allowed("127.0.0.1"))
        #expect(!RemotePeerPolicy.allowed("::1"))
        #expect(RemotePeerPolicy.allowed("127.0.0.1", allowLoopback: true))
        #expect(RemotePeerPolicy.allowed("::1", allowLoopback: true))
        #expect(!RemotePeerPolicy.allowed("192.168.0.2", allowLoopback: true))
        #expect(RemotePeerPolicy.allowed("100.90.1.1"))
        // The interface a peer's address arrives with does not change what it is.
        #expect(RemotePeerPolicy.allowed("fd7a:115c:a1e0::5%utun4"))
        #expect(!RemotePeerPolicy.allowed("fe80::1%utun4"))
    }

    @Test func addressesParseWithTheDefaultPort() throws {
        #expect(try RemoteAddress.parse("100.101.102.103") == RemoteAddress(host: "100.101.102.103", port: 43137))
        #expect(try RemoteAddress.parse(" http://100.1.2.3:5000/ ").display == "100.1.2.3:5000")
        #expect(try RemoteAddress.parse("My-Mac.tail1234.ts.net").display == "my-mac.tail1234.ts.net:43137")
        #expect(try RemoteAddress.parse("studio:43200").display == "studio:43200")
        #expect(try RemoteAddress.parse("[fd7a:115c:a1e0::1]:43200").display == "[fd7a:115c:a1e0::1]:43200")
        #expect(try RemoteAddress.parse("fd7a:115c:a1e0:0::1").display == "[fd7a:115c:a1e0::1]:43137")
        for bad in ["", "https://100.1.2.3", "ftp://x", "user@100.1.2.3", "100.1.2.3/path", "100.1.2.3?k=v", "100.1.2.3#x", "100.1.2.3:80", "100.1.2.3:99999",
                    "100.1.2.3:abc", "[fd7a::1", "-host", "a..b", "host name", "fe80::1%en0", String(repeating: "a", count: 400)] {
            #expect(throws: MightyError.self, "\(bad)") { _ = try RemoteAddress.parse(bad) }
        }
    }
}

struct RemoteWorkspaceKeyTests {
    @Test func keysHaveTheirShapeAndCompareWhole() {
        let key = String(repeating: "A", count: 43)
        #expect(RemoteWorkspaceValidation.key(key))
        #expect(!RemoteWorkspaceValidation.key(String(repeating: "A", count: 42)))
        #expect(!RemoteWorkspaceValidation.key(key + "+"))
        #expect(!RemoteWorkspaceValidation.key(String(repeating: "A", count: 129)))
        #expect(RemoteWorkspaceValidation.keysMatch(key, key))
        #expect(!RemoteWorkspaceValidation.keysMatch(key, key + "B"))
        #expect(!RemoteWorkspaceValidation.keysMatch(String(repeating: "B", count: 43), key))
        #expect(!RemoteWorkspaceValidation.keysMatch("", key))
    }

    @Test func onlyAnAuthorizedRunRequestGetsTheAttachmentBody() {
        let key = String(repeating: "k", count: 43)
        func head(_ target: String = "/v2/runs", method: String = "POST", headers: [String: String] = [:], peer: String = "100.90.1.1") -> HTTPRequest {
            var values = ["authorization": "Bearer " + key, "x-mighty-remote-version": "2"]
            for (name, value) in headers { values[name] = value }
            return HTTPRequest(method: method, target: target, headers: values, body: Data(), remoteAddress: peer)
        }
        #expect(RemoteHostService.bodyLimit(head(), key: key, allowLoopback: false) == AttachmentSupport.maximumRequestBytes)
        #expect(RemoteHostService.bodyLimit(head(headers: ["authorization": "Bearer " + String(repeating: "x", count: 43)]), key: key, allowLoopback: false) == 0)
        #expect(RemoteHostService.bodyLimit(head(headers: ["x-mighty-remote-version": "1"]), key: key, allowLoopback: false) == 0)
        #expect(RemoteHostService.bodyLimit(head(headers: ["origin": "https://example.com"]), key: key, allowLoopback: false) == 0)
        #expect(RemoteHostService.bodyLimit(head("/v2/runs/x/steer"), key: key, allowLoopback: false) == RemoteProtocol.maximumRequestBytes)
        #expect(RemoteHostService.bodyLimit(head(peer: "192.168.1.5"), key: key, allowLoopback: false) == 0)
        #expect(RemoteHostService.bodyLimit(head(peer: "127.0.0.1"), key: key, allowLoopback: false) == 0)
        #expect(RemoteHostService.bodyLimit(head(peer: "127.0.0.1"), key: key, allowLoopback: true) == AttachmentSupport.maximumRequestBytes)
    }

    @Test func hostRunEventsAreCheckedBeforeTheClientUsesThem() {
        let entry = LogEntry(kind: "assistant", text: "hi", provider: "claude")
        #expect(RemoteWorkspaceValidation.event(RunEvent(sessionId: "job", type: "log", entry: entry), sessionId: "job"))
        #expect(!RemoteWorkspaceValidation.event(RunEvent(sessionId: "other", type: "log", entry: entry), sessionId: "job"))
        #expect(!RemoteWorkspaceValidation.event(RunEvent(sessionId: "job", type: "log", entry: LogEntry(kind: "secret", text: "x")), sessionId: "job"))
        #expect(!RemoteWorkspaceValidation.event(RunEvent(sessionId: "job", type: "status", status: "exploded"), sessionId: "job"))
        #expect(!RemoteWorkspaceValidation.event(RunEvent(sessionId: "job", type: "unknown"), sessionId: "job"))
        #expect(RemoteWorkspaceValidation.event(RunEvent(sessionId: "job", type: "status", status: "error", reason: "auth"), sessionId: "job"))
        // A picture's cache key is the host's; the client never keeps one.
        var pictured = entry; pictured.images = [AgentImageRef(hash: String(repeating: "a", count: 64), mediaType: "image/png", width: 1, height: 1, bytes: 1, source: "Read")]
        #expect(RemoteClientService.local(RunEvent(sessionId: "job", type: "log", entry: pictured), pane: "pane")?.entry?.images == nil)
        #expect(RemoteClientService.local(RunEvent(sessionId: "job", type: "log", entry: pictured), pane: "pane")?.sessionId == "pane")
    }

    @Test func clientSettingsForModelsAreChecked() {
        #expect(RemoteWorkspaceValidation.phaseModels(PhaseModelHardcodedConfig()))
        #expect(RemoteWorkspaceValidation.phaseModels(PhaseModelHardcodedConfig(claudeMain: "claude-opus-5-5")))
        #expect(!RemoteWorkspaceValidation.phaseModels(PhaseModelHardcodedConfig(claudeMain: "--dangerous flag")))
        var effort = PhaseModelHardcodedConfig(); effort.claudeMainEffort = "ludicrous"
        #expect(!RemoteWorkspaceValidation.phaseModels(effort))
    }
}

struct RemoteWorkspaceStateTests {
    private func object(_ remote: String) -> String {
        #"{"version":1,"workspaces":[{"id":"local","name":"Local","path":"/tmp","createdAt":"2026-01-01T00:00:00Z"},{"id":"far","name":"Far","path":"/Users/host/proj","createdAt":"2026-01-01T00:00:00Z","remote":"# + remote + #"}],"sessions":[{"id":"r1","workspaceId":"far","title":"Claude"}],"activeWorkspaceId":"far"}"#
    }

    @Test func todaysReferencesComeBackAsRemoteAndOlderOnesAreDropped() throws {
        let current = StateRepository.decodeSnapshot(Data(object(#"{"version":2,"connectionId":"conn-1","workspaceId":"w1","hostName":"studio"}"#).utf8))
        #expect(current.workspaces.map(\.id) == ["local", "far"])
        #expect(current.workspaces[1].remote == RemoteWorkspaceReference(connectionId: "conn-1", workspaceId: "w1", hostName: "studio"))
        #expect(current.workspaces[0].remote == nil)
        #expect(current.sessions.map(\.id) == ["r1"])
        // Before 2026-09-28 a reference had no version; any other version or shape is not ours either.
        for stale in [#"{"connectionId":"conn-1","workspaceId":"w1","hostName":"studio"}"#, #"{"version":1,"connectionId":"c","workspaceId":"w","hostName":"h"}"#,
                      #"{"version":"2","connectionId":"c","workspaceId":"w","hostName":"h"}"#, #"{"version":true,"connectionId":"c","workspaceId":"w","hostName":"h"}"#,
                      #"{"version":2,"connectionId":"bad id","workspaceId":"w","hostName":"h"}"#, #"{"version":2,"connectionId":"c","workspaceId":"w","hostName":""}"#, #""studio""#] {
            let snapshot = StateRepository.decodeSnapshot(Data(object(stale).utf8))
            #expect(snapshot.workspaces.map(\.id) == ["local"], "\(stale)")
            #expect(snapshot.sessions.isEmpty, "\(stale)")
        }
    }

    @Test func aLocalWorkspaceIsNeverWrittenOrReadAsRemote() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-remote-state-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true); defer { try? FileManager.default.removeItem(at: root) }
        let repository = StateRepository(directory: root.appendingPathComponent("state"), legacyStateURL: nil)
        let local = try await repository.approveWorkspace(Workspace(name: "Local", path: root.path))
        let far = try await repository.approveRemoteWorkspace(connectionId: "conn-1", hostWorkspaceId: "w1", name: "Far", path: "C:\\work\\proj", hostName: "studio")
        // The same host workspace through the same connection is the one already listed.
        #expect(try await repository.approveRemoteWorkspace(connectionId: "conn-1", hostWorkspaceId: "w1", name: "Far", path: "C:\\work\\proj", hostName: "studio").id == far.id)
        await #expect(throws: MightyError.self) { _ = try await repository.resolveLocalWorkspace(id: far.id) }
        await #expect(throws: MightyError.self) { _ = try await repository.approveWorkspace(far) }
        try await repository.save(AppSnapshot(workspaces: [local, far], sessions: [RunSession(workspaceId: far.id, title: "Claude")]))
        let text = try String(contentsOf: root.appendingPathComponent("state/workspace-state.json"), encoding: .utf8)
        #expect(text.components(separatedBy: "\"remote\"").count == 2)
        let restored = try await StateRepository(directory: root.appendingPathComponent("state"), legacyStateURL: nil).load()
        #expect(restored.workspaces.first { $0.id == local.id }?.remote == nil)
        #expect(restored.workspaces.first { $0.id == far.id }?.remote?.hostName == "studio")
        #expect(restored.sessions.count == 1)
        // A saved remote workspace cannot be turned local by a save, nor the other way.
        var forged = far; forged.remote = nil
        await #expect(throws: MightyError.self) { try await repository.save(AppSnapshot(workspaces: [forged])) }
        var claimed = local; claimed.remote = RemoteWorkspaceReference(connectionId: "conn-1", workspaceId: "w9", hostName: "studio")
        await #expect(throws: MightyError.self) { try await repository.save(AppSnapshot(workspaces: [claimed])) }
    }

    @Test func thePhoneSeesOnlyThisMacsWorkspaces() {
        let local = Workspace(id: "local", name: "L", path: "/tmp")
        let far = Workspace(id: "far", name: "F", path: "/x", remote: RemoteWorkspaceReference(connectionId: "c", workspaceId: "w", hostName: "h"))
        let sessions = [RunSession(workspaceId: "local", title: "A"), RunSession(workspaceId: "far", title: "B")]
        #expect(RemoteWorkspacePhone.workspaces([local, far]).map(\.id) == ["local"])
        #expect(RemoteWorkspacePhone.sessions(sessions, workspaces: [local, far]).map(\.title) == ["A"])
    }

    @Test func aRunnerNeverStartsInARemoteWorkspace() async throws {
        let runner = ProcessRunner(providerService: ProviderService(), pluginDirectory: URL(fileURLWithPath: "/tmp")) { _ in }
        let far = Workspace(id: "far", name: "F", path: "/tmp", remote: RemoteWorkspaceReference(connectionId: "c", workspaceId: "w", hostName: "h"))
        await #expect(throws: MightyError.self) { try await runner.start(request: StartRunRequest(sessionId: "p", workspaceId: "far", kind: "shell", input: "echo hi"), workspace: far) }
        await runner.shutdown()
    }
}

// MARK: - Over loopback

private final class EventLog: @unchecked Sendable {
    private let lock = NSLock()
    private var items: [RunEvent] = []
    func append(_ event: RunEvent) { lock.lock(); items.append(event); lock.unlock() }
    var values: [RunEvent] { lock.lock(); defer { lock.unlock() }; return items }
    func ended(_ pane: String) -> RunEvent? { values.last { $0.sessionId == pane && $0.type == "status" && ["completed", "error", "stopped"].contains($0.status ?? "") } }
}

/// The HTTP status a remote call failed with; nil when it succeeded or failed otherwise.
private func failureStatus(_ operation: () async throws -> Void) async -> Int? {
    do { try await operation(); return nil } catch { return (error as? RemoteHTTPFailure)?.status }
}

private func remoteWait(_ seconds: Double = 30, _ condition: () async -> Bool) async throws {
    let deadline = Date().addingTimeInterval(seconds)
    while !(await condition()) {
        guard Date() < deadline else { throw MightyError("Timed out waiting for the remote run") }
        try await Task.sleep(for: .milliseconds(25))
    }
}

/// One raw HTTP/1.1 exchange, for what URLSession will not send.
private func rawExchange(port: Int, _ request: String) throws -> String {
    let socketFD = socket(AF_INET, SOCK_STREAM, 0)
    guard socketFD >= 0 else { throw MightyError("socket") }
    defer { close(socketFD) }
    var address = sockaddr_in(); address.sin_family = sa_family_t(AF_INET); address.sin_port = in_port_t(UInt16(port).bigEndian); address.sin_addr.s_addr = inet_addr("127.0.0.1")
    let connected = withUnsafePointer(to: &address) { $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { connect(socketFD, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) } }
    guard connected == 0 else { throw MightyError("connect") }
    var timeout = timeval(tv_sec: 10, tv_usec: 0)
    setsockopt(socketFD, SOL_SOCKET, SO_RCVTIMEO, &timeout, socklen_t(MemoryLayout<timeval>.size))
    let bytes = Array(request.utf8)
    _ = bytes.withUnsafeBytes { write(socketFD, $0.baseAddress, $0.count) }
    var response = Data(); var buffer = [UInt8](repeating: 0, count: 4096)
    while true { let count = read(socketFD, &buffer, buffer.count); if count <= 0 { break }; response.append(contentsOf: buffer.prefix(count)) }
    return String(decoding: response, as: UTF8.self)
}

@Suite(.serialized) struct RemoteWorkspaceLoopbackTests {
    private struct Fixture {
        let root: URL
        let repository: StateRepository
        let workspace: Workspace
        let providers: ProviderService
        let host: RemoteHostService
        let clientRepository: StateRepository
        let client: RemoteClientService
        let events: EventLog
        func cleanup() async { await client.shutdown(); _ = await host.stop(); await providers.shutdown(); try? FileManager.default.removeItem(at: root) }
    }

    /// A claude stand-in that asks for one tool, waits for the answer, delegates
    /// to an agent and answers, in Claude Code's stream-json, over stdin and stdout.
    private static let fakeClaude = #"""
    #!/bin/sh
    if [ "$1" = "--version" ]; then printf '2.1.289\n'; exit 0; fi
    metadata=false
    for argument in "$@"; do if [ "$argument" = "--safe-mode" ]; then metadata=true; fi; done
    IFS= read -r initialize || exit 21
    request_id=$(printf '%s' "$initialize" | /usr/bin/sed -E 's/.*"request_id":"([^"]+)".*/\1/')
    printf '{"type":"control_response","response":{"subtype":"success","request_id":"%s","response":{"models":[]}}}\n' "$request_id"
    if [ "$metadata" = true ]; then /bin/cat >/dev/null; exit 0; fi
    IFS= read -r prompt || exit 22
    printf '%s\n' "$@" > arguments.txt
    printf '%s\n' '{"type":"system","subtype":"init","session_id":"main-session","model":"claude-opus-5-5"}'
    printf '%s\n' '{"type":"control_request","request_id":"perm-1","request":{"subtype":"can_use_tool","tool_name":"Bash","tool_use_id":"toolu_bash","input":{"command":"ls"}}}'
    IFS= read -r response || exit 23
    printf '%s\n' "$response" > response.json
    printf '%s\n' '{"type":"assistant","uuid":"main","session_id":"main-session","message":{"content":[{"type":"tool_use","id":"child","name":"Agent","input":{"prompt":"Look around","description":"Scout"}}]}}'
    printf '%s\n' '{"type":"assistant","uuid":"child-msg","session_id":"child-session","parent_tool_use_id":"child","message":{"content":[{"type":"text","text":"Child says hi"}]}}'
    printf '%s\n' '{"type":"result","parent_tool_use_id":"child","is_error":false,"result":"Child done"}'
    printf '%s\n' '{"type":"assistant","uuid":"main-2","session_id":"main-session","message":{"content":[{"type":"text","text":"Answer from the host"}]}}'
    printf '%s\n' '{"type":"result","subtype":"success","session_id":"main-session","is_error":false,"result":"Answer from the host"}'
    while IFS= read -r extra; do :; done
    """#

    private func fixture(heartbeat: TimeInterval = RemoteProtocol.heartbeatTimeout, admission: @escaping @Sendable (StartRunRequest) async -> String? = { _ in nil }) async throws -> Fixture {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-remote-\(UUID().uuidString)")
        let folder = root.appendingPathComponent("project")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let binary = root.appendingPathComponent("claude")
        try Data(Self.fakeClaude.utf8).write(to: binary)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: binary.path)
        let plugin = root.appendingPathComponent("plugin")
        try FileManager.default.createDirectory(at: plugin.appendingPathComponent(".claude-plugin"), withIntermediateDirectories: true)
        try Data("{}".utf8).write(to: plugin.appendingPathComponent(".claude-plugin/plugin.json"))
        let repository = StateRepository(directory: root.appendingPathComponent("host"), legacyStateURL: nil)
        let workspace = try await repository.approveWorkspace(Workspace(name: "Project", path: folder.path))
        let hidden = try await repository.approveWorkspace(Workspace(name: "Private", path: root.appendingPathComponent("plugin").path))
        try await repository.save(AppSnapshot(workspaces: [workspace, hidden]))
        let providers = ProviderService(binaryOverrides: ["claude": binary])
        let host = RemoteHostService(repository: repository, providers: providers, pluginDirectory: plugin, appVersion: "9.9.9", hostName: "Test host",
                                     allowLoopbackForTests: true, heartbeat: heartbeat, admission: admission)
        let events = EventLog()
        let clientRepository = StateRepository(directory: root.appendingPathComponent("client"), legacyStateURL: nil)
        let client = RemoteClientService(repository: clientRepository, dataDirectory: root.appendingPathComponent("client"), allowLoopbackForTests: true) { events.append($0) }
        return Fixture(root: root, repository: repository, workspace: workspace, providers: providers, host: host, clientRepository: clientRepository, client: client, events: events)
    }

    private func share(_ fixture: Fixture, fullAccess: Bool = true) async throws -> (key: String, port: Int) {
        let state = try await fixture.host.start(workspaceIds: [fixture.workspace.id], port: 0)
        let key = try #require(state.key), port = try #require(state.port)
        #expect(state.enabled && state.addresses == ["127.0.0.1:\(port)"] && !state.allowsFullAccess)
        // Most tests run command panes, which the host refuses until this is on.
        if fullAccess { #expect(await fixture.host.setAllowsFullAccess(true).allowsFullAccess) }
        return (key, port)
    }

    @Test func aClientListsSharedWorkspacesAndRunsCommandsOnTheHost() async throws {
        let fixture = try await fixture()
        do {
            let (key, port) = try await share(fixture)
            let info = try await fixture.client.connect(name: "", address: "127.0.0.1:\(port)", key: key)
            #expect(info.status == .connected && info.hostName == "Test host" && info.name == "Test host")
            // Only what the host chose to share is listed.
            #expect(info.workspaces.map(\.id) == [fixture.workspace.id])
            #expect(info.runtime?.appVersion == "9.9.9")
            let opened = try await fixture.client.openWorkspace(connectionId: info.id, hostWorkspaceId: fixture.workspace.id)
            #expect(opened.remote?.workspaceId == fixture.workspace.id && opened.path == fixture.workspace.path && opened.id != fixture.workspace.id)
            // A command pane runs on the host, in the shared folder.
            try await fixture.client.start(request: StartRunRequest(sessionId: "shell-pane", workspaceId: opened.id, kind: "shell", input: "pwd; echo remote-ok"), workspace: opened, phaseModels: nil)
            try await remoteWait { fixture.events.ended("shell-pane") != nil }
            #expect(fixture.events.ended("shell-pane")?.status == "completed")
            let output = fixture.events.values.filter { $0.sessionId == "shell-pane" && $0.entry?.kind == "output" }.compactMap(\.entry?.text).joined()
            #expect(output.contains("remote-ok"))
            #expect(output.contains(URL(fileURLWithPath: fixture.workspace.path).resolvingSymlinksInPath().lastPathComponent))
            // Stop reaches the host's process.
            try await fixture.client.start(request: StartRunRequest(sessionId: "sleeper", workspaceId: opened.id, kind: "shell", input: "sleep 30"), workspace: opened, phaseModels: nil)
            try await remoteWait { await fixture.host.current().activeRuns == 1 }
            await fixture.client.stop(id: "sleeper")
            #expect(fixture.events.ended("sleeper")?.status == "stopped")
            try await remoteWait { await fixture.host.current().activeRuns == 0 }
            await fixture.cleanup()
        } catch { await fixture.cleanup(); throw error }
    }

    @Test func aRemoteClaudeRunFeedsTheLocalViewsAndAsksTheClient() async throws {
        let fixture = try await fixture()
        do {
            let (key, port) = try await share(fixture)
            let info = try await fixture.client.connect(name: "Studio", address: "127.0.0.1:\(port)", key: key)
            let opened = try await fixture.client.openWorkspace(connectionId: info.id, hostWorkspaceId: fixture.workspace.id)
            var session = RunSession(workspaceId: opened.id, title: "Claude")
            session.beginGraphRun(input: "Look around", id: "request-1", configuredModel: "default")
            var request = StartRunRequest(sessionId: session.id, workspaceId: opened.id, input: "Look around", model: "opus", settings: RunSettings(effort: "high", permissionMode: "manual"))
            request.phaseModels = PhaseModelConfig()
            try await fixture.client.start(request: request, workspace: opened, phaseModels: PhaseModelHardcodedConfig(claudeSubagentDefault: "sonnet"))
            // The host's tool request is answered from the client, as a local pane's is.
            try await remoteWait { fixture.events.values.contains { $0.sessionId == session.id && $0.permission?.state == "pending" } }
            let permission = try #require(fixture.events.values.first { $0.sessionId == session.id && $0.permission?.state == "pending" }?.permission)
            #expect(permission.toolName == "Bash")
            try await fixture.client.respondToPermission(sessionId: session.id, runId: permission.runId, requestId: permission.id, allow: true)
            try await remoteWait { fixture.events.ended(session.id) != nil }
            #expect(fixture.events.ended(session.id)?.status == "completed")
            let response = try String(contentsOf: URL(fileURLWithPath: fixture.workspace.path).appendingPathComponent("response.json"), encoding: .utf8)
            #expect(response.contains("allow"))
            // Model, effort and permission mode reached the host's CLI.
            let arguments = try String(contentsOf: URL(fileURLWithPath: fixture.workspace.path).appendingPathComponent("arguments.txt"), encoding: .utf8)
            #expect(arguments.contains("--model\nopus\n") && arguments.contains("--permission-mode\nmanual\n") && arguments.contains("--permission-prompts\nhost\n"))
            #expect(arguments.contains("--effort\nhigh\n") && arguments.contains("\"CLAUDE_CODE_SUBAGENT_MODEL\":\"sonnet\""))
            // The same events a local run gives: the transcript and the Mighty graph.
            for event in fixture.events.values where event.sessionId == session.id {
                session.recordGraph(event)
                if event.type == "log", let entry = event.entry { session.logs.append(entry) }
                if event.type == "resume" { session.resumeId = event.resumeId }
            }
            #expect(session.logs.contains { $0.kind == "assistant" && $0.text == "Answer from the host" })
            #expect(session.resumeId == "main-session")
            let graph = try #require(session.mightyGraphRuns.last)
            #expect(graph.finalOutput == "Answer from the host")
            #expect(graph.agents.contains { $0.title.contains("Scout") || $0.input.contains("Look around") })
            await fixture.cleanup()
        } catch { await fixture.cleanup(); throw error }
    }

    @Test func wrongKeysUnsharedWorkspacesAndOversizeRequestsAreRefused() async throws {
        let fixture = try await fixture()
        do {
            let (key, port) = try await share(fixture)
            let target = RemoteTarget(address: RemoteAddress(host: "127.0.0.1", port: port), ip: "127.0.0.1", port: port)
            #expect(await failureStatus { _ = try await RemoteTransport.request(target, key: String(repeating: "Z", count: 43), method: "GET", path: "/v2/info") } == 401)
            #expect(await failureStatus { _ = try await fixture.client.connect(name: "", address: "127.0.0.1:\(port)", key: String(repeating: "Z", count: 43)) } == 401)
            // An old app's protocol is not answered.
            let old = try rawExchange(port: port, "GET /v1/info HTTP/1.1\r\nHost: x\r\nAuthorization: Bearer \(key)\r\nx-mighty-remote-version: 1\r\n\r\n")
            #expect(old.hasPrefix("HTTP/1.1 426") || old.hasPrefix("HTTP/1.1 400"))
            // A browser page is never answered, even with the key.
            let browser = try rawExchange(port: port, "GET /v2/info HTTP/1.1\r\nHost: x\r\nOrigin: https://example.com\r\nAuthorization: Bearer \(key)\r\nx-mighty-remote-version: 2\r\n\r\n")
            #expect(browser.hasPrefix("HTTP/1.1 403"))
            // Over the plain limit without the key, and over it on any other route even with the key.
            let big = RemoteProtocol.maximumRequestBytes + 1
            let unauthorized = try rawExchange(port: port, "POST /v2/runs HTTP/1.1\r\nHost: x\r\nContent-Type: application/json\r\nContent-Length: \(big)\r\nx-mighty-remote-version: 2\r\n\r\n")
            // Without the key no body is read at all: the head alone is answered.
            #expect(unauthorized.hasPrefix("HTTP/1.1 401"))
            let otherRoute = try rawExchange(port: port, "POST /v2/runs/job/steer HTTP/1.1\r\nHost: x\r\nAuthorization: Bearer \(key)\r\nContent-Type: application/json\r\nContent-Length: \(big)\r\nx-mighty-remote-version: 2\r\n\r\n")
            #expect(otherRoute.hasPrefix("HTTP/1.1 413"))
            // A workspace the host did not share is never a working folder.
            let snapshot = try await fixture.repository.load()
            let hidden = try #require(snapshot.workspaces.first { $0.id != fixture.workspace.id })
            let body = try JSONEncoder().encode(RemoteWireStart(request: StartRunRequest(sessionId: "x", workspaceId: hidden.id, kind: "shell", input: "touch escaped")))
            #expect(await failureStatus { _ = try await RemoteTransport.request(target, key: key, method: "POST", path: "/v2/runs", body: body) } == 403)
            #expect(!FileManager.default.fileExists(atPath: URL(fileURLWithPath: hidden.path).appendingPathComponent("escaped").path))
            // Settings the host's CLI does not have are refused before anything runs.
            let fast = try JSONEncoder().encode(RemoteWireStart(request: StartRunRequest(sessionId: "y", workspaceId: fixture.workspace.id, input: "hi", settings: RunSettings(webSearch: "live"))))
            #expect(await failureStatus { _ = try await RemoteTransport.request(target, key: key, method: "POST", path: "/v2/runs", body: fast) } == 400)
            // Stopping revokes the key: the same key is refused from then on.
            _ = await fixture.host.stop()
            await #expect(throws: (any Error).self) { _ = try await RemoteTransport.request(target, key: key, method: "GET", path: "/v2/info") }
            let again = try await fixture.host.start(workspaceIds: [fixture.workspace.id], port: 0)
            #expect(again.key != key)
            let newTarget = RemoteTarget(address: RemoteAddress(host: "127.0.0.1", port: again.port!), ip: "127.0.0.1", port: again.port!)
            #expect(await failureStatus { _ = try await RemoteTransport.request(newTarget, key: key, method: "GET", path: "/v2/info") } == 401)
            await fixture.cleanup()
        } catch { await fixture.cleanup(); throw error }
    }

    @Test func aRunNobodyPollsIsStoppedAndADroppedHostEndsTheClientsRuns() async throws {
        let fixture = try await fixture(heartbeat: 1)
        do {
            let (key, port) = try await share(fixture)
            let target = RemoteTarget(address: RemoteAddress(host: "127.0.0.1", port: port), ip: "127.0.0.1", port: port)
            let body = try JSONEncoder().encode(RemoteWireStart(request: StartRunRequest(sessionId: "x", workspaceId: fixture.workspace.id, kind: "shell", input: "sleep 30")))
            let job = try JSONDecoder().decode(RemoteWireJob.self, from: try await RemoteTransport.request(target, key: key, method: "POST", path: "/v2/runs", body: body)).jobId
            try await remoteWait { await fixture.host.current().activeRuns == 1 }
            // Nobody polls: the heartbeat runs out and the host stops it.
            try await remoteWait(15) { await fixture.host.current().activeRuns == 0 }
            let poll = try JSONDecoder().decode(RemoteWirePoll.self, from: try await RemoteTransport.request(target, key: key, method: "GET", path: "/v2/runs/\(job)/events?cursor=0"))
            #expect(poll.done && poll.events.last?.event.status == "stopped")

            let info = try await fixture.client.connect(name: "", address: "127.0.0.1:\(port)", key: key)
            let opened = try await fixture.client.openWorkspace(connectionId: info.id, hostWorkspaceId: fixture.workspace.id)
            try await fixture.client.start(request: StartRunRequest(sessionId: "pane", workspaceId: opened.id, kind: "shell", input: "sleep 30"), workspace: opened, phaseModels: nil)
            try await remoteWait { await fixture.host.current().activeRuns == 1 }
            // The host goes away mid-run: the pane ends in an error and the connection says so.
            _ = await fixture.host.stop()
            try await remoteWait { fixture.events.ended("pane") != nil }
            #expect(fixture.events.ended("pane")?.status == "error")
            #expect(fixture.events.values.contains { $0.sessionId == "pane" && $0.entry?.kind == "error" })
            #expect(await fixture.client.connection(info.id)?.status == .disconnected)
            await fixture.cleanup()
        } catch { await fixture.cleanup(); throw error }
    }

    @Test func aHostBusyWithItsOwnUpdateHoldsClientRuns() async throws {
        let fixture = try await fixture(admission: { request in request.kind == "shell" ? nil : "updating" })
        do {
            let (key, port) = try await share(fixture)
            let target = RemoteTarget(address: RemoteAddress(host: "127.0.0.1", port: port), ip: "127.0.0.1", port: port)
            let agent = try JSONEncoder().encode(RemoteWireStart(request: StartRunRequest(sessionId: "a", workspaceId: fixture.workspace.id, input: "hi")))
            #expect(await failureStatus { _ = try await RemoteTransport.request(target, key: key, method: "POST", path: "/v2/runs", body: agent) } == 503)
            #expect(RemoteClientService.transient(RemoteHTTPFailure(status: 503, message: "")) && RemoteClientService.transient(URLError(.timedOut)))
            #expect(!RemoteClientService.transient(RemoteHTTPFailure(status: 401, message: "")) && !RemoteClientService.transient(MightyError("bad")))
            #expect(await fixture.host.current().activeRuns == 0)
            await fixture.cleanup()
        } catch { await fixture.cleanup(); throw error }
    }

    @Test func redirectsAreNeverFollowed() async throws {
        let hits = EventLog()
        let elsewhere = HTTPServer(address: "127.0.0.1", port: 0) { _ in hits.append(RunEvent(sessionId: "hit", type: "log")); return HTTPResponse(status: 200, body: Data("{}".utf8), headers: ["x-mighty-remote-version": "2"]) }
        let elsewherePort = try await elsewhere.start()
        let redirecting = HTTPServer(address: "127.0.0.1", port: 0) { _ in
            HTTPResponse(status: 302, body: Data(), headers: ["Location": "http://127.0.0.1:\(elsewherePort)/v2/info", "x-mighty-remote-version": "2"])
        }
        let port = Int(try await redirecting.start())
        let target = RemoteTarget(address: RemoteAddress(host: "127.0.0.1", port: port), ip: "127.0.0.1", port: port)
        await #expect(throws: MightyError.self) { _ = try await RemoteTransport.request(target, key: String(repeating: "k", count: 43), method: "GET", path: "/v2/info") }
        #expect(hits.values.isEmpty)
        await redirecting.stop(); await elsewhere.stop()
    }

    @Test func savedConnectionsKeepNoKey() async throws {
        let fixture = try await fixture()
        do {
            let (key, port) = try await share(fixture)
            let info = try await fixture.client.connect(name: "Studio", address: "http://127.0.0.1:\(port)", key: key)
            let saved = try String(contentsOf: fixture.root.appendingPathComponent("client/remote-connections.json"), encoding: .utf8)
            #expect(saved.contains("127.0.0.1:\(port)") && saved.contains("Studio") && !saved.contains(key))
            // A fresh client lists it as saved, not connected, until it is used.
            let later = RemoteClientService(repository: fixture.clientRepository, dataDirectory: fixture.root.appendingPathComponent("client"), allowLoopbackForTests: true) { _ in }
            #expect(await later.list().map(\.status) == [.saved])
            #expect(await later.list().first?.id == info.id)
            await fixture.client.remove(id: info.id)
            #expect(await fixture.client.list().isEmpty)
            await fixture.cleanup()
        } catch { await fixture.cleanup(); throw error }
    }

    @Test func commandsAndFullAccessWaitForTheHostsSwitch() async throws {
        let fixture = try await fixture()
        do {
            let (key, port) = try await share(fixture, fullAccess: false)
            let info = try await fixture.client.connect(name: "", address: "127.0.0.1:\(port)", key: key)
            let opened = try await fixture.client.openWorkspace(connectionId: info.id, hostWorkspaceId: fixture.workspace.id)
            // Off: a command pane and a full-access agent are refused before anything runs, and the client says why.
            let shell = await failureStatus { try await fixture.client.start(request: StartRunRequest(sessionId: "c", workspaceId: opened.id, kind: "shell", input: "touch ran"), workspace: opened, phaseModels: nil) }
            #expect(shell == 403)
            let bypass = StartRunRequest(sessionId: "b", workspaceId: opened.id, input: "hi", settings: RunSettings(permissionMode: "fullAccess"))
            #expect(await failureStatus { try await fixture.client.start(request: bypass, workspace: opened, phaseModels: nil) } == 403)
            #expect(!FileManager.default.fileExists(atPath: URL(fileURLWithPath: fixture.workspace.path).appendingPathComponent("ran").path))
            #expect(RemoteHostService.fullAccessRefusal(StartRunRequest(sessionId: "x", workspaceId: "w", input: "hi", provider: "codex", settings: RunSettings(permissionMode: "fullAccess")), allowed: false) != nil)
            #expect(RemoteHostService.fullAccessRefusal(StartRunRequest(sessionId: "x", workspaceId: "w", input: "hi", provider: "gemini", settings: RunSettings(permissionMode: "fullAccess")), allowed: false) != nil)
            #expect(RemoteHostService.fullAccessRefusal(StartRunRequest(sessionId: "x", workspaceId: "w", input: "hi", settings: RunSettings(permissionMode: "acceptEdits")), allowed: false) == nil)
            // On: the command runs. Stopping and sharing again turns it off.
            _ = await fixture.host.setAllowsFullAccess(true)
            try await fixture.client.start(request: StartRunRequest(sessionId: "c2", workspaceId: opened.id, kind: "shell", input: "touch ran"), workspace: opened, phaseModels: nil)
            try await remoteWait { fixture.events.ended("c2") != nil }
            #expect(fixture.events.ended("c2")?.status == "completed")
            _ = await fixture.host.stop()
            #expect(try await fixture.host.start(workspaceIds: [fixture.workspace.id], port: 0).allowsFullAccess == false)
            await fixture.cleanup()
        } catch { await fixture.cleanup(); throw error }
    }

    @Test func aRouteThatLeavesTheTunnelEndsTheConnection() async throws {
        let fixture = try await fixture()
        do {
            let (key, port) = try await share(fixture)
            let tunnel = RouteSeam()
            let client = RemoteClientService(repository: fixture.clientRepository, dataDirectory: fixture.root.appendingPathComponent("route-client"), allowLoopbackForTests: true,
                                             routeCheck: { _ in tunnel.up }) { fixture.events.append($0) }
            let info = try await client.connect(name: "", address: "127.0.0.1:\(port)", key: key)
            let opened = try await client.openWorkspace(connectionId: info.id, hostWorkspaceId: fixture.workspace.id)
            try await client.start(request: StartRunRequest(sessionId: "routed", workspaceId: opened.id, kind: "shell", input: "sleep 30"), workspace: opened, phaseModels: nil)
            try await remoteWait { await fixture.host.current().activeRuns == 1 }
            // The route moves off the tunnel mid-run: no further request goes out, the run ends in an error.
            tunnel.up = false
            try await remoteWait { fixture.events.ended("routed") != nil }
            #expect(fixture.events.ended("routed")?.status == "error")
            #expect(await client.connection(info.id)?.status == .disconnected)
            // Connected or not, the next use checks the route first, and it is not a passing failure.
            await #expect(throws: MightyError.self) { _ = try await client.ensureConnected(id: info.id) }
            #expect(!RemoteClientService.transient(MightyError(L("remote.connect.error.notViaTailscale"))))
            await client.shutdown()
            await fixture.cleanup()
        } catch { await fixture.cleanup(); throw error }
    }
}

private final class RouteSeam: @unchecked Sendable {
    private let lock = NSLock()
    private var value = true
    var up: Bool {
        get { lock.lock(); defer { lock.unlock() }; return value }
        set { lock.lock(); value = newValue; lock.unlock() }
    }
}

/// The host's HTTP server refuses at accept, before reading anything.
struct RemoteWorkspaceServerTests {
    @Test func aRefusedPeerIsCutOffAtAccept() async throws {
        let refused = HTTPServer(address: "127.0.0.1", port: 0, acceptPeer: { _ in false }) { _ in HTTPResponse(status: 200, body: Data("{}".utf8)) }
        let port = Int(try await refused.start())
        #expect(try rawExchange(port: port, "GET / HTTP/1.1\r\nHost: x\r\n\r\n").isEmpty)
        await refused.stop()
        let allowed = HTTPServer(address: "127.0.0.1", port: 0, acceptPeer: { RemotePeerPolicy.allowed($0, allowLoopback: true) }) { _ in HTTPResponse(status: 200, body: Data("{}".utf8)) }
        let open = Int(try await allowed.start())
        #expect(try rawExchange(port: open, "GET / HTTP/1.1\r\nHost: x\r\n\r\n").hasPrefix("HTTP/1.1 200"))
        await allowed.stop()
    }

    @Test func onePeerHoldsAtMostItsShareOfConnections() async throws {
        let server = HTTPServer(address: "127.0.0.1", port: 0, maximumConnectionsPerPeer: 2) { _ in HTTPResponse(status: 200, body: Data("{}".utf8)) }
        let port = Int(try await server.start())
        let held = try (0..<2).map { _ in try openSocket(port: port) }
        try await Task.sleep(for: .milliseconds(200))
        // A third connection from the same peer is closed at once…
        let third = try openSocket(port: port)
        #expect(readsEndOfFile(third, within: 2))
        close(third)
        // …while the two it holds are still open.
        #expect(!readsEndOfFile(held[0], within: 0.3))
        // One closes: its place is free again.
        close(held[0]); try await Task.sleep(for: .milliseconds(300))
        #expect(try rawExchange(port: port, "GET / HTTP/1.1\r\nHost: x\r\n\r\n").hasPrefix("HTTP/1.1 200"))
        close(held[1])
        await server.stop()
    }

    @Test func noBodyIsReadWithoutTheKey() async throws {
        let seen = EventLog()
        let server = HTTPServer(address: "127.0.0.1", port: 0, requestBodyLimit: { RemoteHostService.bodyLimit($0, key: String(repeating: "k", count: 43), allowLoopback: true) }) { request in
            seen.append(RunEvent(sessionId: "\(request.body.count)", type: "log"))
            return HTTPResponse(status: 401, body: Data("{}".utf8))
        }
        let port = Int(try await server.start())
        // The head says a large body follows; none is sent, and the answer comes anyway.
        let answer = try rawExchange(port: port, "POST /v2/runs HTTP/1.1\r\nHost: x\r\nAuthorization: Bearer \(String(repeating: "z", count: 43))\r\nContent-Type: application/json\r\nContent-Length: 100000\r\nx-mighty-remote-version: 2\r\n\r\n")
        #expect(answer.hasPrefix("HTTP/1.1 401"))
        #expect(seen.values.map(\.sessionId) == ["0"])
        await server.stop()
    }

    @Test func pendingRequestsAndUnusableEventsAreBounded() async throws {
        let requests = (0..<40).map { ToolPermissionRequest(id: "r\($0)", runId: "run", toolUseId: "t\($0)", toolName: "Bash", inputJSON: "{}", summary: "s") }
        let kept = RemotePendingPermissions.capped(requests)
        #expect(kept.count == RemoteProtocol.maximumPendingPermissions && kept.first?.id == "r8" && kept.last?.id == "r39")
        // A host that sends one event the client cannot use: that event is left out, the run goes on.
        let job = "job-1", key = String(repeating: "k", count: 43)
        let info = try JSONEncoder().encode(RemoteWireInfo(hostId: "host-1", hostName: "Fake", workspaces: [RemoteSharedWorkspace(id: "w1", name: "W", path: "/tmp")], runtime: RuntimeInfo()))
        let poll = try JSONEncoder().encode(RemoteWirePoll(cursor: 2, lastCursor: 2, gap: false, done: true, events: [
            RemoteWireEvent(cursor: 1, event: RunEvent(sessionId: job, type: "nonsense")),
            RemoteWireEvent(cursor: 2, event: RunEvent(sessionId: job, type: "status", status: "completed"))]))
        let fake = HTTPServer(address: "127.0.0.1", port: 0) { request in
            let body = request.target.hasSuffix("/info") ? info : request.target.contains("/events") ? poll : try! JSONEncoder().encode(RemoteWireJob(jobId: job))
            return HTTPResponse(status: request.target == "/v2/runs" ? 202 : 200, body: body, headers: [RemoteProtocol.header: "\(RemoteProtocol.version)"])
        }
        let port = Int(try await fake.start())
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-remote-skip-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: root) }
        let events = EventLog()
        let client = RemoteClientService(repository: StateRepository(directory: root, legacyStateURL: nil), dataDirectory: root, allowLoopbackForTests: true) { events.append($0) }
        let connection = try await client.connect(name: "", address: "127.0.0.1:\(port)", key: key)
        let opened = try await client.openWorkspace(connectionId: connection.id, hostWorkspaceId: "w1")
        try await client.start(request: StartRunRequest(sessionId: "pane", workspaceId: opened.id, kind: "shell", input: "ls"), workspace: opened, phaseModels: nil)
        try await remoteWait { events.ended("pane") != nil }
        #expect(events.ended("pane")?.status == "completed")
        #expect(events.values.contains { $0.entry?.text == L("remote.run.notice.skipped") })
        #expect(await client.connection(connection.id)?.status == .connected)
        await client.shutdown(); await fake.stop()
    }
}

private func openSocket(port: Int) throws -> Int32 {
    let socketFD = socket(AF_INET, SOCK_STREAM, 0)
    guard socketFD >= 0 else { throw MightyError("socket") }
    var address = sockaddr_in(); address.sin_family = sa_family_t(AF_INET); address.sin_port = in_port_t(UInt16(port).bigEndian); address.sin_addr.s_addr = inet_addr("127.0.0.1")
    let connected = withUnsafePointer(to: &address) { $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { connect(socketFD, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) } }
    guard connected == 0 else { close(socketFD); throw MightyError("connect") }
    return socketFD
}

/// Whether the peer closed the socket within `seconds` (a read gets end of file or a reset).
private func readsEndOfFile(_ socketFD: Int32, within seconds: Double) -> Bool {
    var timeout = timeval(tv_sec: Int(seconds), tv_usec: Int32((seconds - Double(Int(seconds))) * 1_000_000))
    setsockopt(socketFD, SOL_SOCKET, SO_RCVTIMEO, &timeout, socklen_t(MemoryLayout<timeval>.size))
    var byte: UInt8 = 0
    let count = read(socketFD, &byte, 1)
    return count == 0 || (count < 0 && errno != EAGAIN && errno != EWOULDBLOCK)
}

/// native/contracts/fixtures/tailscale-addresses.json, which the Windows Core checks read too
/// (TailscaleAddressesVerification): the same interface lists give each platform the addresses the
/// file says. The Mac also requires a `utun` name; Windows cannot (adapters are renamable), so the
/// two answers differ only where the file says so.
struct RemoteWorkspaceAddressFixtureTests {
    struct Fixture: Decodable { let contains: Contains; let cases: [Case] }
    struct Contains: Decodable { let accepted: [String]; let rejected: [String] }
    struct Case: Decodable { let name: String; let interfaces: [Interface]; let expected: [String: [String]] }
    struct Interface: Decodable { let name: String; let addresses: [String] }

    static let url: URL = {
        var url = URL(fileURLWithPath: #filePath)
        for _ in 0..<5 { url.deleteLastPathComponent() }
        return url.appendingPathComponent("native/contracts/fixtures/tailscale-addresses.json")
    }()

    static func fixture() throws -> Fixture { try JSONDecoder().decode(Fixture.self, from: Data(contentsOf: url)) }

    @Test func everyAddressIsInOrOutOfTheRangesAsTheFileSays() throws {
        let fixture = try Self.fixture()
        #expect(!fixture.contains.accepted.isEmpty && !fixture.contains.rejected.isEmpty)
        for address in fixture.contains.accepted { #expect(TailscaleAddresses.contains(address), "\(address)") }
        for address in fixture.contains.rejected { #expect(!TailscaleAddresses.contains(address), "\(address)") }
    }

    @Test func everyInterfaceListSelectsTheMacsExpectedAddresses() throws {
        let cases = try Self.fixture().cases
        #expect(cases.count >= 12)
        for item in cases {
            let read = item.interfaces.flatMap { interface in interface.addresses.map { (name: interface.name, address: $0) } }
            #expect(item.expected["darwin"] != nil && item.expected["win32"] != nil, "\(item.name) names both platforms")
            #expect(TailscaleAddresses.select(interfaces: read) == item.expected["darwin"], "\(item.name)")
        }
    }
}
