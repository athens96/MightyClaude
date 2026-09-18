import CryptoKit
import Darwin
import Foundation
import Testing
@testable import MightyCore

/// Drives the real Node relay (relay/dist/server.js) with the host service on
/// one side and a phone-shaped client written here on the other. Skipped when
/// the relay has not been built.
private final class StaticHost: MobileHostDelegate, @unchecked Sendable {
    func mobileState() async -> MobileState { MobileState(revision: 3, hostName: "Relay Mac", workspaces: [], sessions: []) }
    func mobileSession(id: String) async -> MobileSessionDetail? { nil }
    func mobileSubmit(sessionId: String, text: String, mode: String?, attachments: [RunAttachment]) async throws -> String { "started" }
    func mobileGuided(sessionId: String, style: String, skill: String, text: String) async throws -> String { "started" }
    func mobileStop(sessionId: String) async throws {}
    func mobilePermission(sessionId: String, requestId: String, runId: String, allow: Bool) async throws {}
    func mobileAnswers(sessionId: String, requestId: String, runId: String, answers: [String: UserQuestionAnswer]) async throws {}
    func mobileCreateSession(workspaceId: String, kind: String, provider: String) async throws -> String { "new" }
    func mobileRemoveQueued(sessionId: String, itemId: String) async throws {}
    func mobileRunNextQueued(sessionId: String) async throws {}
    func mobileRename(sessionId: String, title: String) async throws {}
    func mobileClose(sessionId: String) async throws {}
    func mobileEntries(sessionId: String, before: String, limit: Int) async throws -> MobileEntriesPage { MobileEntriesPage(entries: [], hasMore: false) }
    func mobileApplySettings(sessionId: String, request: MobileSettingsRequest) async throws {}
    func mobileCommands(sessionId: String) async throws -> [MobileCommand] { [] }
    func mobilePerformCommand(sessionId: String, action: String) async throws -> String? { nil }
}

/// One phone-shaped connection: the socket and the cipher that keeps talking on
/// it. A reference type, so a test can hold two of them open at the same time
/// without threading two `inout` ciphers through every call.
private final class RelayPhone: @unchecked Sendable {
    let socket: URLSessionWebSocketTask
    private var cipher: RelayCipher
    init(socket: URLSessionWebSocketTask, cipher: RelayCipher) { self.socket = socket; self.cipher = cipher }

    func send(_ object: [String: Any]) async throws {
        try await socket.send(.data(try cipher.seal(try JSONSerialization.data(withJSONObject: object))))
    }
    func receive() async throws -> [String: Any] {
        guard case .data(let frame) = try await socket.receive() else { throw MightyError("바이너리 프레임이 아닙니다.") }
        return try JSONSerialization.jsonObject(with: cipher.open(frame)) as? [String: Any] ?? [:]
    }
    func close() { socket.cancel(with: .normalClosure, reason: nil) }
    /// Whether the far end hung up. URLSession only surfaces a close frame
    /// through a pending read, so one read is left in flight while the state is
    /// polled: awaiting that read directly would block forever on a socket that
    /// is still alive, and "still alive" is exactly the failure to report here.
    func waitUntilClosed(seconds: Double) async -> Bool {
        let reader = Task { [socket] in _ = try? await socket.receive() }
        defer { reader.cancel() }
        let deadline = Date().addingTimeInterval(seconds)
        while Date() < deadline {
            if socket.closeCode != .invalid || socket.state != .running { return true }
            try? await Task.sleep(for: .milliseconds(50))
        }
        return socket.closeCode != .invalid || socket.state != .running
    }
}

struct RelayIntegrationTests {
    private static var relayScript: URL? {
        var url = URL(fileURLWithPath: #filePath)
        for _ in 0..<5 { url.deleteLastPathComponent() } // file → MightyCoreTests → Tests → macos → native → repo
        let script = url.appendingPathComponent("relay/dist/server.js")
        return FileManager.default.fileExists(atPath: script.path) ? script : nil
    }
    private static var node: URL? {
        ["/opt/homebrew/bin/node", "/usr/local/bin/node"].map(URL.init(fileURLWithPath:)).first { FileManager.default.isExecutableFile(atPath: $0.path) }
    }
    /// The harness needs the built relay and a node to run it. Without either —
    /// a checkout that never built relay/dist, a machine with no node — these
    /// tests are reported as skipped instead of quietly passing on nothing.
    private static var relayHarnessAvailable: Bool { relayScript != nil && node != nil }

    /// A port the kernel handed out rather than one guessed at random: bind to
    /// 0, read the number back, let it go again. The relay claims it a moment
    /// later, so two suites running in parallel cannot pick the same number.
    private static func freePort() -> Int? {
        let handle = Darwin.socket(AF_INET, SOCK_STREAM, 0)
        guard handle >= 0 else { return nil }
        defer { Darwin.close(handle) }
        var address = sockaddr_in()
        address.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        address.sin_family = sa_family_t(AF_INET)
        address.sin_port = 0
        address.sin_addr.s_addr = inet_addr("127.0.0.1")
        let length = socklen_t(MemoryLayout<sockaddr_in>.size)
        let bound = withUnsafePointer(to: &address) { pointer in
            pointer.withMemoryRebound(to: sockaddr.self, capacity: 1) { Darwin.bind(handle, $0, length) }
        }
        guard bound == 0 else { return nil }
        var assigned = sockaddr_in()
        var assignedLength = length
        let named = withUnsafeMutablePointer(to: &assigned) { pointer in
            pointer.withMemoryRebound(to: sockaddr.self, capacity: 1) { Darwin.getsockname(handle, $0, &assignedLength) }
        }
        guard named == 0 else { return nil }
        return Int(UInt16(bigEndian: assigned.sin_port))
    }

    /// The relay, listening and answering /healthz. The window between letting
    /// the probe port go and the relay taking it is small but real, so a relay
    /// that never comes up is retried on a fresh port rather than failed.
    private func startRelay(script: URL, node: URL) async throws -> (relay: NativeChildProcess, port: Int) {
        for _ in 0..<3 {
            guard let port = Self.freePort() else { continue }
            let relay = try NativeChildProcess(executable: node, arguments: [script.path], environment: ["PORT": String(port), "HOST": "127.0.0.1", "PATH": "/usr/bin:/bin", "RELAY_ATTACH_TIMEOUT_MS": "5000"],
                                               cwd: script.deletingLastPathComponent(), stdout: { _ in }, stderr: { _ in }, exited: { _ in })
            if await waitForHealthz(port: port) { return (relay, port) }
            relay.stop()
        }
        throw MightyError("릴레이가 수신 포트에서 응답하지 않았습니다.")
    }
    private func waitForHealthz(port: Int) async -> Bool {
        for _ in 0..<50 {
            if let (data, _) = try? await URLSession.shared.data(from: URL(string: "http://127.0.0.1:\(port)/healthz")!), String(decoding: data, as: UTF8.self) == "ok" { return true }
            try? await Task.sleep(for: .milliseconds(100))
        }
        return false
    }

    private func receiveText(_ socket: URLSessionWebSocketTask) async throws -> [String: Any] {
        guard case .string(let text) = try await socket.receive(), let object = try JSONSerialization.jsonObject(with: Data(text.utf8)) as? [String: Any] else { throw MightyError("텍스트 프레임이 아닙니다.") }
        return object
    }
    private func receiveEncrypted(_ socket: URLSessionWebSocketTask, _ cipher: inout RelayCipher) async throws -> [String: Any] {
        guard case .data(let frame) = try await socket.receive() else { throw MightyError("바이너리 프레임이 아닙니다.") }
        return try JSONSerialization.jsonObject(with: cipher.open(frame)) as? [String: Any] ?? [:]
    }
    private func sendEncrypted(_ socket: URLSessionWebSocketTask, _ cipher: inout RelayCipher, _ object: [String: Any]) async throws {
        try await socket.send(.data(try cipher.seal(try JSONSerialization.data(withJSONObject: object))))
    }

    /// One full phone-side connection: handshake, then the given auth frame,
    /// then whatever the host answered. The phone stays open for the caller.
    private func dial(offer: MobilePairingOffer, frame: [String: Any]) async throws -> (phone: RelayPhone, reply: [String: Any]) {
        let key = Curve25519.KeyAgreement.PrivateKey(), nonce = RelayCrypto.randomBytes(16)
        let url = try #require(RelayEndpoint.socketURL(relay: offer.relayURL, serverId: offer.serverId, role: "client", connectionId: UUID().uuidString.lowercased()))
        let socket = URLSession.shared.webSocketTask(with: url)
        socket.resume()
        let hello = try JSONSerialization.data(withJSONObject: ["type": "hello", "v": 1, "clientKey": key.publicKey.rawRepresentation.base64EncodedString(), "nonce": nonce.base64EncodedString()])
        try await socket.send(.string(String(decoding: hello, as: UTF8.self)))
        let ready = try await receiveText(socket)
        let serverNonce = try #require((ready["nonce"] as? String).flatMap { Data(base64Encoded: $0) })
        let cipher = try RelayCipher(privateKey: key, peerPublicKey: try #require(Data(base64Encoded: offer.publicKeyB64)), clientNonce: nonce, serverNonce: serverNonce, isHost: false)
        let phone = RelayPhone(socket: socket, cipher: cipher)
        try await phone.send(frame)
        return (phone, try await phone.receive())
    }

    /// The host service, attached and connected to the relay, for as long as
    /// `body` runs — and shut down afterwards on every path, a failed
    /// expectation and a thrown error included. `defer` cannot await, so the
    /// teardown is spelled out on both exits instead.
    private func withHost(directory: URL, port: Int, delegate: StaticHost,
                          _ body: (MobileRemoteService, MobilePairingOffer) async throws -> Void) async throws {
        let service = MobileRemoteService(dataDirectory: directory, hostName: "Relay Mac", appVersion: "1.2.3")
        do {
            await service.attach(delegate) // the service holds its delegate weakly, as the app store does
            _ = await service.apply(settings: MobileRemoteSettings(enabled: true, relayURL: "ws://127.0.0.1:\(port)"))
            var status = await service.status()
            for _ in 0..<50 where !status.relayConnected { try await Task.sleep(for: .milliseconds(100)); status = await service.status() }
            #expect(status.relayConnected)
            let offer = try #require(status.pairingURL.flatMap(MobilePairingOffer.parse))
            try await body(service, offer)
        } catch {
            await service.shutdown()
            throw error
        }
        await service.shutdown()
    }

    /// Settles until the host reports exactly `count` live phones, so the next
    /// step never races a socket that is still being torn down or set up.
    private func waitForClients(_ service: MobileRemoteService, count: Int) async -> Int {
        var clients = await service.status().clients
        for _ in 0..<80 where clients != count {
            try? await Task.sleep(for: .milliseconds(50))
            clients = await service.status().clients
        }
        return clients
    }

    @Test(.enabled(if: RelayIntegrationTests.relayHarnessAvailable))
    func phoneReachesTheHostThroughTheRelayWithEndToEndEncryption() async throws {
        let script = try #require(Self.relayScript)
        let node = try #require(Self.node)
        let (relay, port) = try await startRelay(script: script, node: node)
        defer { relay.stop() }
        try #require(await waitForHealthz(port: port))

        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("relay-int-" + UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let host = StaticHost()
        try await withHost(directory: directory, port: port, delegate: host) { service, offer in
            let serverId = await service.status().serverId
            #expect(offer.relayURL == "ws://127.0.0.1:\(port)" && offer.serverId == serverId)

            // Phone side.
            let clientKey = Curve25519.KeyAgreement.PrivateKey()
            let clientNonce = RelayCrypto.randomBytes(16)
            let url = try #require(RelayEndpoint.socketURL(relay: offer.relayURL, serverId: offer.serverId, role: "client", connectionId: UUID().uuidString.lowercased()))
            let socket = URLSession.shared.webSocketTask(with: url)
            socket.resume()
            try await socket.send(.string(String(decoding: try JSONSerialization.data(withJSONObject: ["type": "hello", "v": 1, "clientKey": clientKey.publicKey.rawRepresentation.base64EncodedString(), "nonce": clientNonce.base64EncodedString()]), as: UTF8.self)))
            let ready = try await receiveText(socket)
            #expect(ready["type"] as? String == "ready" && ready["serverKey"] as? String == offer.publicKeyB64)
            let serverNonce = try #require((ready["nonce"] as? String).flatMap { Data(base64Encoded: $0) })
            var cipher = try RelayCipher(privateKey: clientKey, peerPublicKey: Data(base64Encoded: offer.publicKeyB64)!, clientNonce: clientNonce, serverNonce: serverNonce, isHost: false)
            let clientId = "cGhvbmUtaW50ZWctMDE"
            try await sendEncrypted(socket, &cipher, ["type": "auth", "pairingKey": offer.pairingKey, "clientId": clientId, "clientName": "Test phone"])
            let ok = try await receiveEncrypted(socket, &cipher)
            #expect(ok["type"] as? String == "auth_ok" && ok["hostName"] as? String == "Relay Mac" && ok["appVersion"] as? String == "1.2.3")
            // First pairing hands the device token over exactly once.
            let deviceToken = try #require(ok["deviceToken"] as? String)
            #expect(MobileDeviceRegistry.validToken(deviceToken))
            let listed = await service.status().devices
            #expect(listed.map(\.id) == [clientId] && listed.first?.name == "Test phone" && listed.first?.connected == true)

            try await sendEncrypted(socket, &cipher, ["id": "r1", "method": "GET", "path": "/m1/state?since=0&wait=0"])
            let reply = try await receiveEncrypted(socket, &cipher)
            #expect(reply["id"] as? String == "r1" && reply["status"] as? Int == 200 && (reply["body"] as? [String: Any])?["revision"] as? Int == 3)
            #expect(await waitForClients(service, count: 1) == 1)

            // Host-initiated notify reaches the phone; ping is answered.
            await service.notify(scope: "state", revision: 4)
            let notify = try await receiveEncrypted(socket, &cipher)
            #expect(notify["type"] as? String == "notify" && notify["scope"] as? String == "state" && notify["revision"] as? Int == 4)
            try await sendEncrypted(socket, &cipher, ["type": "ping"])
            let pong = try await receiveEncrypted(socket, &cipher)
            #expect(pong["type"] as? String == "pong")
            // A POST with a body and an unknown route travel the same way.
            try await sendEncrypted(socket, &cipher, ["id": "r2", "method": "POST", "path": "/m1/sessions/s1/submit", "body": ["text": "hi"]])
            let posted = try await receiveEncrypted(socket, &cipher)
            #expect(posted["status"] as? Int == 202)
            try await sendEncrypted(socket, &cipher, ["id": "r3", "method": "GET", "path": "/m1/nothing"])
            let missing = try await receiveEncrypted(socket, &cipher)
            #expect(missing["status"] as? Int == 404)
            socket.cancel(with: .normalClosure, reason: nil)

            // A wrong pairing key is told so and dropped.
            let badKey = Curve25519.KeyAgreement.PrivateKey(), badNonce = RelayCrypto.randomBytes(16)
            let badSocket = URLSession.shared.webSocketTask(with: try #require(RelayEndpoint.socketURL(relay: offer.relayURL, serverId: offer.serverId, role: "client", connectionId: UUID().uuidString.lowercased())))
            badSocket.resume()
            try await badSocket.send(.string(String(decoding: try JSONSerialization.data(withJSONObject: ["type": "hello", "v": 1, "clientKey": badKey.publicKey.rawRepresentation.base64EncodedString(), "nonce": badNonce.base64EncodedString()]), as: UTF8.self)))
            let badReady = try await receiveText(badSocket)
            var badCipher = try RelayCipher(privateKey: badKey, peerPublicKey: Data(base64Encoded: offer.publicKeyB64)!, clientNonce: badNonce, serverNonce: Data(base64Encoded: badReady["nonce"] as! String)!, isHost: false)
            try await sendEncrypted(badSocket, &badCipher, ["type": "auth", "pairingKey": String(repeating: "x", count: 43)])
            let refused = try await receiveEncrypted(badSocket, &badCipher)
            #expect(refused["type"] as? String == "auth_error")
            badSocket.cancel(with: .normalClosure, reason: nil)

            // The phone comes back with its token and no pairing key at all, and no
            // second token is issued.
            let again = try await dial(offer: offer, frame: ["type": "auth", "clientId": clientId, "deviceToken": deviceToken, "clientName": "Test phone"])
            #expect(again.reply["type"] as? String == "auth_ok" && again.reply["deviceToken"] == nil)
            again.phone.close()

            // Revoked: the entry is gone, the pairing key is new, and the token is
            // refused with the reason the app watches for.
            let after = try await service.revokeDevice(clientId)
            #expect(after.devices.isEmpty && after.key != offer.pairingKey)
            let revoked = try await dial(offer: offer, frame: ["type": "auth", "clientId": clientId, "deviceToken": deviceToken])
            #expect(revoked.reply["type"] as? String == "auth_error" && revoked.reply["reason"] as? String == "device-revoked")
            revoked.phone.close()
        }
        withExtendedLifetime(host) {}
    }

    /// Two phones on one Mac, end to end through the relay: revoking the first
    /// must cut its live socket and refuse its token, while the second — which
    /// never presented the pairing key the revoke rotates — keeps answering on
    /// the socket it already has and can still come back on its own token.
    /// Every step waits for the host's own client count before the next one, so
    /// two simultaneously open sockets stay deterministic rather than racing.
    @Test(.enabled(if: RelayIntegrationTests.relayHarnessAvailable))
    func revokingOnePhoneLeavesTheOtherConnectedAndAbleToReturn() async throws {
        let script = try #require(Self.relayScript)
        let node = try #require(Self.node)
        let (relay, port) = try await startRelay(script: script, node: node)
        defer { relay.stop() }
        try #require(await waitForHealthz(port: port))

        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("relay-two-" + UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let host = StaticHost()
        try await withHost(directory: directory, port: port, delegate: host) { service, offer in
            let phoneA = "cGhvbmUtaW50ZWctQUFB", phoneB = "cGhvbmUtaW50ZWctQkJC"

            // Pairing, one phone at a time: each pairing socket is closed and
            // accounted for before the next phone dials.
            let pairA = try await dial(offer: offer, frame: ["type": "auth", "pairingKey": offer.pairingKey, "clientId": phoneA, "clientName": "Phone A"])
            #expect(pairA.reply["type"] as? String == "auth_ok")
            let tokenA = try #require(pairA.reply["deviceToken"] as? String)
            #expect(await waitForClients(service, count: 1) == 1)
            pairA.phone.close()
            #expect(await waitForClients(service, count: 0) == 0)

            // Pairing does not rotate the key, but B pairs with whatever the host
            // is offering now rather than with a value captured before A existed.
            let keyForB = try #require(await service.status().key)
            let pairB = try await dial(offer: offer, frame: ["type": "auth", "pairingKey": keyForB, "clientId": phoneB, "clientName": "Phone B"])
            #expect(pairB.reply["type"] as? String == "auth_ok")
            let tokenB = try #require(pairB.reply["deviceToken"] as? String)
            #expect(await waitForClients(service, count: 1) == 1)
            pairB.phone.close()
            #expect(await waitForClients(service, count: 0) == 0)
            let paired = await service.status().devices
            #expect(Set(paired.map(\.id)) == [phoneA, phoneB] && tokenA != tokenB)

            // Both come back on their tokens and stay open together.
            let liveA = try await dial(offer: offer, frame: ["type": "auth", "clientId": phoneA, "deviceToken": tokenA, "clientName": "Phone A"])
            #expect(liveA.reply["type"] as? String == "auth_ok" && liveA.reply["deviceToken"] == nil)
            #expect(await waitForClients(service, count: 1) == 1)
            let liveB = try await dial(offer: offer, frame: ["type": "auth", "clientId": phoneB, "deviceToken": tokenB, "clientName": "Phone B"])
            #expect(liveB.reply["type"] as? String == "auth_ok" && liveB.reply["deviceToken"] == nil)
            #expect(await waitForClients(service, count: 2) == 2)
            try await liveA.phone.send(["id": "a1", "method": "GET", "path": "/m1/state?since=0&wait=0"])
            let beforeA = try await liveA.phone.receive()
            #expect(beforeA["id"] as? String == "a1" && beforeA["status"] as? Int == 200)
            try await liveB.phone.send(["id": "b1", "method": "GET", "path": "/m1/state?since=0&wait=0"])
            let beforeB = try await liveB.phone.receive()
            #expect(beforeB["id"] as? String == "b1" && beforeB["status"] as? Int == 200)

            let keyBeforeRevoke = try #require(await service.status().key)
            let after = try await service.revokeDevice(phoneA)
            #expect(after.devices.map(\.id) == [phoneB] && after.key != keyBeforeRevoke)

            // A's live socket goes with the row.
            #expect(await liveA.phone.waitUntilClosed(seconds: 6))
            #expect(await waitForClients(service, count: 1) == 1)

            // B never presented the key, so the rotation left its socket alone: the
            // same connection still answers, on the cipher it has been using.
            try await liveB.phone.send(["id": "b2", "method": "GET", "path": "/m1/state?since=0&wait=0"])
            let survivor = try await liveB.phone.receive()
            #expect(survivor["id"] as? String == "b2" && survivor["status"] as? Int == 200)
            #expect((survivor["body"] as? [String: Any])?["revision"] as? Int == 3)

            // A's token is refused with the reason the app watches for; B's still works.
            let returningA = try await dial(offer: offer, frame: ["type": "auth", "clientId": phoneA, "deviceToken": tokenA, "clientName": "Phone A"])
            #expect(returningA.reply["type"] as? String == "auth_error" && returningA.reply["reason"] as? String == "device-revoked")
            returningA.phone.close()
            let returningB = try await dial(offer: offer, frame: ["type": "auth", "clientId": phoneB, "deviceToken": tokenB, "clientName": "Phone B"])
            #expect(returningB.reply["type"] as? String == "auth_ok" && returningB.reply["deviceToken"] == nil)
            returningB.phone.close()
            liveB.phone.close()
        }
        withExtendedLifetime(host) {}
    }
}
