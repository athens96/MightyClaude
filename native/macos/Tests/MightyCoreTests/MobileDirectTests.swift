import CryptoKit
import Darwin
import Foundation
import Testing
@testable import MightyCore

/// The direct path's pure parts: how endpoints are written, and what the QR
/// and the saved settings carry. Which addresses count as Tailscale is
/// `TailscaleAddresses`' rule, tested with the remote workspaces.
struct MobileDirectTests {
    @Test func endpointsAreWrittenAndCheckedTheSameWayEverywhere() {
        #expect(MobileDirect.endpoint(host: "100.101.102.103", port: 43139) == "100.101.102.103:43139")
        #expect(MobileDirect.endpoint(host: "fd7a:115c:a1e0::1", port: 43139) == "[fd7a:115c:a1e0::1]:43139")
        #expect(MobileDirect.validEndpoint("100.101.102.103:43139"))
        #expect(MobileDirect.validEndpoint("[fd7a:115c:a1e0::1]:43139"))
        #expect(MobileDirect.validEndpoint("studio.tail1234.ts.net:43139"))
        #expect(!MobileDirect.validEndpoint("fd7a:115c:a1e0::1:43139"))
        #expect(!MobileDirect.validEndpoint("192.168.0.5:43139"))
        #expect(!MobileDirect.validEndpoint("127.0.0.1:43139"))
        #expect(!MobileDirect.validEndpoint("studio.example.com:43139"))
        #expect(!MobileDirect.validEndpoint("100.101.102.103"))
        #expect(!MobileDirect.validEndpoint("100.101.102.103:0"))
        #expect(!MobileDirect.validEndpoint("100.101.102.103:65536"))
        #expect(MobileDirect.isTailnetName("studio.tail1234.ts.net") && !MobileDirect.isTailnetName("ts.net") && !MobileDirect.isTailnetName("Studio.ts.net"))

        let advertised = MobileDirect.advertised(bound: [("100.101.102.103", 43139), ("fd7a:115c:a1e0::1", 43139)], name: "studio.tail1234.ts.net", port: 43139)
        #expect(advertised == ["100.101.102.103:43139", "[fd7a:115c:a1e0::1]:43139", "studio.tail1234.ts.net:43139"])
        // A name alone, with nothing bound, is not advertised; loopback never is outside the test seam.
        #expect(MobileDirect.advertised(bound: [], name: "studio.tail1234.ts.net", port: 43139).isEmpty)
        #expect(MobileDirect.advertised(bound: [("127.0.0.1", 5000)], name: nil, port: 5000).isEmpty)
        #expect(MobileDirect.advertised(bound: [("127.0.0.1", 5000)], name: nil, port: 5000, allowAnyHost: true) == ["127.0.0.1:5000"])
    }

    @Test func pairingOfferCarriesDirectPercentEncodedAndOlderOffersStayTheSame() throws {
        let key = try #require(MobilePairing.generateKey())
        let keypair = RelayKeypair()
        let plain = MobilePairingOffer(serverId: "host-1", publicKeyB64: keypair.publicKeyB64, relayURL: "wss://relay.example.com", pairingKey: key, name: "Mac")
        #expect(!plain.url.contains("direct="))
        #expect(MobilePairingOffer.parse(plain.url)?.directEndpoints == [])

        let endpoints = ["100.101.102.103:43139", "[fd7a:115c:a1e0::1]:43139", "studio.tail1234.ts.net:43139"]
        let offer = MobilePairingOffer(serverId: "host-1", publicKeyB64: keypair.publicKeyB64, relayURL: "wss://relay.example.com", pairingKey: key, name: "Mac", directEndpoints: endpoints)
        #expect(offer.url.contains("&direct=100.101.102.103%3A43139%2C%5Bfd7a%3A115c%3Aa1e0%3A%3A1%5D%3A43139%2Cstudio.tail1234.ts.net%3A43139"))
        #expect(MobilePairingOffer.parse(offer.url) == offer)
        // What is not a Tailscale endpoint is dropped on the way in.
        let mixed = plain.url + "&direct=" + "192.168.0.5%3A43139%2C100.64.0.1%3A43139"
        #expect(MobilePairingOffer.parse(mixed)?.directEndpoints == ["100.64.0.1:43139"])
    }

    @Test func settingsDefaultToDirectOnAndKeepAValidPort() throws {
        let saved = try JSONDecoder().decode(MobileRemoteSettings.self, from: Data(#"{"enabled":true,"relayURL":"wss://relay.example.com"}"#.utf8))
        #expect(saved.directEnabled && saved.directPort == MobileDirect.defaultPort)
        #expect(MobileRemoteSettings(directPort: 80).normalized.directPort == MobileDirect.defaultPort)
        #expect(MobileRemoteSettings(directPort: 50_000).normalized.directPort == 50_000)
        let encoded = try JSONEncoder().encode(MobileRemoteSettings(enabled: true, directEnabled: false, directPort: 50_000))
        let back = try JSONDecoder().decode(MobileRemoteSettings.self, from: encoded)
        #expect(!back.directEnabled && back.directPort == 50_000)
    }
}

private final class DirectHost: MobileHostDelegate, @unchecked Sendable {
    func mobileState() async -> MobileState { MobileState(revision: 5, hostName: "Direct Mac", workspaces: [], sessions: []) }
    func mobileSession(id: String) async -> MobileSessionDetail? { nil }
    func mobileSubmit(sessionId: String, text: String, mode: String?, attachments: [RunAttachment]) async throws -> String { "started" }
    func mobileGuided(sessionId: String, style: String, skill: String, text: String) async throws -> String { "started" }
    func mobileStop(sessionId: String) async throws -> Bool { true }
    func mobilePermission(sessionId: String, requestId: String, runId: String, allow: Bool) async throws {}
    func mobileAnswers(sessionId: String, requestId: String, runId: String, answers: [String: UserQuestionAnswer]) async throws {}
    func mobileCreateSession(workspaceId: String, kind: String, provider: String) async throws -> String { "new" }
    func mobileRemoveQueued(sessionId: String, itemId: String) async throws {}
    func mobileRunNextQueued(sessionId: String) async throws {}
    func mobileRename(sessionId: String, title: String, titleMode: String?) async throws {}
    func mobileClose(sessionId: String) async throws {}
    func mobileEntries(sessionId: String, before: String, limit: Int) async throws -> MobileEntriesPage { MobileEntriesPage(entries: [], hasMore: false) }
    func mobileApplySettings(sessionId: String, request: MobileSettingsRequest) async throws {}
    func mobileCommands(sessionId: String) async throws -> [MobileCommand] { [] }
    func mobilePerformCommand(sessionId: String, action: String) async throws -> String? { nil }
}

/// The direct listener end to end over loopback. The app only ever binds the
/// detected Tailscale addresses; these tests reach loopback through the
/// internal `setDirectAddressOverride` seam, which neither Settings nor any
/// IPC can set. No relay runs: the direct path does not need one.
struct MobileDirectIntegrationTests {
    /// A port the kernel handed out, let go again for the listener to take.
    private static func freePort() -> Int? {
        let handle = Darwin.socket(AF_INET, SOCK_STREAM, 0)
        guard handle >= 0 else { return nil }
        defer { Darwin.close(handle) }
        var address = sockaddr_in()
        address.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        address.sin_family = sa_family_t(AF_INET)
        address.sin_addr.s_addr = inet_addr("127.0.0.1")
        let length = socklen_t(MemoryLayout<sockaddr_in>.size)
        guard withUnsafePointer(to: &address, { $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { Darwin.bind(handle, $0, length) } }) == 0 else { return nil }
        var assigned = sockaddr_in(), assignedLength = length
        guard withUnsafeMutablePointer(to: &assigned, { $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { Darwin.getsockname(handle, $0, &assignedLength) } }) == 0 else { return nil }
        return Int(UInt16(bigEndian: assigned.sin_port))
    }

    private struct Phone {
        let socket: URLSessionWebSocketTask
        var cipher: RelayCipher
        mutating func send(_ object: [String: Any]) async throws {
            try await socket.send(.data(try cipher.seal(try JSONSerialization.data(withJSONObject: object))))
        }
        mutating func receive() async throws -> [String: Any] {
            guard case .data(let frame) = try await socket.receive() else { throw MightyError("not binary") }
            return try JSONSerialization.jsonObject(with: cipher.open(frame)) as? [String: Any] ?? [:]
        }
    }

    private static func socket(port: Int, serverId: String) throws -> URLSessionWebSocketTask {
        let url = try #require(RelayEndpoint.socketURL(relay: "ws://127.0.0.1:\(port)", serverId: serverId, role: "client", connectionId: UUID().uuidString.lowercased()))
        let socket = URLSession.shared.webSocketTask(with: url)
        socket.resume()
        return socket
    }

    /// The phone's half of the relay handshake, unchanged, against the direct listener.
    private static func handshake(port: Int, serverId: String, publicKeyB64: String) async throws -> (phone: Phone, serverKey: String) {
        let socket = try socket(port: port, serverId: serverId)
        let key = Curve25519.KeyAgreement.PrivateKey(), nonce = RelayCrypto.randomBytes(16)
        let hello = try JSONSerialization.data(withJSONObject: ["type": "hello", "v": 1, "clientKey": key.publicKey.rawRepresentation.base64EncodedString(), "nonce": nonce.base64EncodedString()])
        try await socket.send(.string(String(decoding: hello, as: UTF8.self)))
        guard case .string(let text) = try await socket.receive(), let ready = try JSONSerialization.jsonObject(with: Data(text.utf8)) as? [String: Any] else { throw MightyError("no ready") }
        let serverKey = try #require(ready["serverKey"] as? String)
        let serverNonce = try #require((ready["nonce"] as? String).flatMap { Data(base64Encoded: $0) })
        let cipher = try RelayCipher(privateKey: key, peerPublicKey: try #require(Data(base64Encoded: publicKeyB64)), clientNonce: nonce, serverNonce: serverNonce, isHost: false)
        return (Phone(socket: socket, cipher: cipher), serverKey)
    }

    /// Whether the host hung up: one read stays in flight while the state is polled.
    private static func closed(_ socket: URLSessionWebSocketTask, within seconds: Double) async -> Bool {
        let reader = Task { _ = try? await socket.receive() }
        defer { reader.cancel() }
        let deadline = Date().addingTimeInterval(seconds)
        while Date() < deadline {
            if socket.closeCode != .invalid || socket.state != .running { return true }
            try? await Task.sleep(for: .milliseconds(50))
        }
        return false
    }

    private static func waitForClients(_ service: MobileRemoteService, count: Int) async -> Int {
        var clients = await service.status().clients
        for _ in 0..<200 where clients != count {
            try? await Task.sleep(for: .milliseconds(50))
            clients = await service.status().clients
        }
        return clients
    }

    @Test func phoneReachesTheHostStraightOverTheDirectListenerWithTheSameEncryption() async throws {
        let port = try #require(Self.freePort())
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("direct-int-" + UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        // The relay field points at a port nothing answers on: only the direct path can work.
        let service = MobileRemoteService(dataDirectory: directory, hostName: "Direct Mac", appVersion: "9.9.9", defaultRelayURL: "", watchesNetwork: false)
        let host = DirectHost()
        await service.attach(host)
        await service.setDirectAddressOverride(["127.0.0.1"])
        var status = await service.apply(settings: MobileRemoteSettings(enabled: true, relayURL: "ws://127.0.0.1:1", directPort: port))
        #expect(status.directListening && status.directEndpoints == ["127.0.0.1:\(port)"])
        // The QR is offered on the direct path alone, with the relay still down.
        #expect(!status.relayConnected && status.pairingURL != nil)
        let offer = try #require(status.pairingURL.flatMap(MobilePairingOffer.parse))

        // Handshake and auth exactly as through the relay.
        var (phone, serverKey) = try await Self.handshake(port: port, serverId: offer.serverId, publicKeyB64: offer.publicKeyB64)
        #expect(serverKey == offer.publicKeyB64)
        let clientId = "cGhvbmUtZGlyZWN0LTAx"
        try await phone.send(["type": "auth", "pairingKey": offer.pairingKey, "clientId": clientId, "clientName": "Direct phone"])
        let ok = try await phone.receive()
        #expect(ok["type"] as? String == "auth_ok" && ok["appVersion"] as? String == "9.9.9")
        #expect((ok["capabilities"] as? [String])?.contains("direct") == true)
        #expect(ok["direct"] as? [String] == ["127.0.0.1:\(port)"])
        let token = try #require(ok["deviceToken"] as? String)
        #expect(MobileDeviceRegistry.validToken(token))

        try await phone.send(["id": "r1", "method": "GET", "path": "/m1/state?since=0&wait=0"])
        let state = try await phone.receive()
        #expect(state["status"] as? Int == 200 && (state["body"] as? [String: Any])?["revision"] as? Int == 5)
        try await phone.send(["id": "r2", "method": "GET", "path": "/m1/info"])
        let info = try await phone.receive()
        #expect((info["body"] as? [String: Any])?["direct"] as? [String] == ["127.0.0.1:\(port)"])
        #expect(await Self.waitForClients(service, count: 1) == 1)
        #expect(await service.status().devices.first?.connected == true)

        // Notifies reach a direct phone too.
        await service.notify(scope: "state", revision: 6)
        let notify = try await phone.receive()
        #expect(notify["type"] as? String == "notify" && notify["revision"] as? Int == 6)

        // A re-detection that finds the same address keeps the listener and the
        // phone already connected, and its later end still leaves the table.
        await service.setDirectAddressOverride(["127.0.0.1"])
        #expect(await service.status().directListening)
        try await phone.send(["id": "r3", "method": "GET", "path": "/m1/state?since=0&wait=0"])
        #expect(try await phone.receive()["status"] as? Int == 200)

        // A frame over the relay's 1 MiB limit ends the connection.
        try? await phone.socket.send(.data(Data(count: MobileDirect.maximumFrameBytes + 1024)))
        #expect(await Self.closed(phone.socket, within: 5))
        #expect(await Self.waitForClients(service, count: 0) == 0)

        // The token alone gets back in, and no second token is issued.
        var (again, _) = try await Self.handshake(port: port, serverId: offer.serverId, publicKeyB64: offer.publicKeyB64)
        try await again.send(["type": "auth", "clientId": clientId, "deviceToken": token, "clientName": "Direct phone"])
        let back = try await again.receive()
        #expect(back["type"] as? String == "auth_ok" && back["deviceToken"] == nil)

        // A wrong pairing key is refused and dropped.
        var (stranger, _) = try await Self.handshake(port: port, serverId: offer.serverId, publicKeyB64: offer.publicKeyB64)
        try await stranger.send(["type": "auth", "pairingKey": String(repeating: "x", count: 43)])
        let refused = try await stranger.receive()
        // One word for every refusal on the direct path.
        #expect(refused["type"] as? String == "auth_error" && refused["reason"] as? String == "unauthorized")
        #expect(await Self.closed(stranger.socket, within: 5))

        // Turning the direct path off closes the phones that came in through it.
        status = await service.apply(settings: MobileRemoteSettings(enabled: true, relayURL: "ws://127.0.0.1:1", directEnabled: false, directPort: port))
        #expect(!status.directListening && status.directEndpoints.isEmpty && status.directDetail == L("mobileRemote.direct.status.off"))
        #expect(await Self.closed(again.socket, within: 5))
        #expect(status.pairingURL == nil)
        await service.shutdown()
        withExtendedLifetime(host) {}
    }

    /// A service listening on loopback through the test seam, for `body`.
    private func withDirect(_ body: (MobileRemoteService, Int, MobileHostStatus) async throws -> Void) async throws {
        let port = try #require(Self.freePort())
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("direct-int-" + UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let service = MobileRemoteService(dataDirectory: directory, hostName: "Direct Mac", defaultRelayURL: "", watchesNetwork: false)
        let host = DirectHost()
        await service.attach(host)
        await service.setDirectAddressOverride(["127.0.0.1"])
        let status = await service.apply(settings: MobileRemoteSettings(enabled: true, relayURL: "ws://127.0.0.1:1", directPort: port))
        #expect(status.directListening)
        do { try await body(service, port, status) } catch { await service.shutdown(); throw error }
        await service.shutdown()
        withExtendedLifetime(host) {}
    }

    /// Opens a plain TCP connection; nil when the connect itself failed.
    private static func rawConnect(port: Int) -> Int32? {
        let handle = Darwin.socket(AF_INET, SOCK_STREAM, 0)
        guard handle >= 0 else { return nil }
        var address = sockaddr_in()
        address.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        address.sin_family = sa_family_t(AF_INET)
        address.sin_port = UInt16(port).bigEndian
        address.sin_addr.s_addr = inet_addr("127.0.0.1")
        let connected = withUnsafePointer(to: &address) { $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { Darwin.connect(handle, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) } }
        guard connected == 0 else { Darwin.close(handle); return nil }
        return handle
    }

    @Test func aBrokenHandshakeIsDroppedAndOnePeerHoldsAtMostTwoHandshakeSlots() async throws {
        try await withDirect { service, port, status in
            // Not a hello: the host hangs up.
            let garbage = try Self.socket(port: port, serverId: status.serverId)
            try await garbage.send(.string(#"{"type":"hello","v":1,"clientKey":"short","nonce":"short"}"#))
            #expect(await Self.closed(garbage, within: 5))
            #expect(await Self.waitForClients(service, count: 0) == 0)

            // Sockets that never say hello: one address holds two handshake
            // slots, and its third socket is dropped unread.
            var stalled: [URLSessionWebSocketTask] = []
            for _ in 0..<MobileDirect.maximumUnauthenticatedPerPeer {
                stalled.append(try Self.socket(port: port, serverId: status.serverId))
                _ = await Self.waitForClients(service, count: stalled.count)
            }
            #expect(await Self.waitForClients(service, count: MobileDirect.maximumUnauthenticatedPerPeer) == MobileDirect.maximumUnauthenticatedPerPeer)
            let extra = try Self.socket(port: port, serverId: status.serverId)
            #expect(await Self.closed(extra, within: 5))
            #expect(await service.status().clients == MobileDirect.maximumUnauthenticatedPerPeer)
            for socket in stalled { socket.cancel(with: .normalClosure, reason: nil) }
            #expect(await Self.waitForClients(service, count: 0) == 0)
        }
    }

    @Test func aFloodOfSocketsStaysBoundedAndEveryEndGivesItsSlotBack() async throws {
        try await withDirect { service, port, _ in
            var handles: [Int32] = []
            var peakLive = 0, peakClients = 0
            for _ in 0..<60 {
                if let handle = Self.rawConnect(port: port) { handles.append(handle) }
                peakLive = max(peakLive, await service.directLiveConnections())
                peakClients = max(peakClients, await service.status().clients)
            }
            #expect(handles.count >= 50)
            // Let the listener work through everything that arrived.
            for _ in 0..<20 {
                try await Task.sleep(for: .milliseconds(50))
                peakLive = max(peakLive, await service.directLiveConnections())
                peakClients = max(peakClients, await service.status().clients)
            }
            // The flood reached the listener, and never held more than its limit.
            #expect(peakLive > 0 && peakLive <= MobileDirectListener.connectionLimit)
            // All from one address: two handshake slots, the rest dropped unread.
            #expect(peakClients <= MobileDirect.maximumUnauthenticatedPerPeer)
            for handle in handles { Darwin.close(handle) }
            var live = await service.directLiveConnections()
            var clients = await service.status().clients
            var waited = 0
            while (live != 0 || clients != 0) && waited < 200 {
                try await Task.sleep(for: .milliseconds(50))
                live = await service.directLiveConnections(); clients = await service.status().clients
                waited += 1
            }
            #expect(live == 0)
            #expect(await service.status().clients == 0)
            // And the listener still takes a phone afterwards.
            let after = try Self.socket(port: port, serverId: "unused")
            #expect(await Self.waitForClients(service, count: 1) == 1)
            after.cancel(with: .normalClosure, reason: nil)
        }
    }

    @Test func anAddressThatKeepsFailingAuthIsDroppedUnread() async throws {
        try await withDirect { service, port, status in
            for _ in 0..<MobileDirect.maximumAuthFailures {
                var (stranger, _) = try await Self.handshake(port: port, serverId: status.serverId, publicKeyB64: try #require(status.publicKeyB64))
                try await stranger.send(["type": "auth", "pairingKey": String(repeating: "x", count: 43), "clientId": "c3RyYW5nZXItcGhvbmUtMDE"])
                let refused = try await stranger.receive()
                #expect(refused["reason"] as? String == "unauthorized")
                #expect(await Self.closed(stranger.socket, within: 5))
                _ = await Self.waitForClients(service, count: 0)
            }
            // The next socket from the same address gets no `ready` at all.
            let blocked = try Self.socket(port: port, serverId: status.serverId)
            #expect(await Self.closed(blocked, within: 5))
            #expect(await service.status().clients == 0)
        }
    }
}
