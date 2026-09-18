import CryptoKit
import Foundation
import Testing
@testable import MightyCore

/// Drives the real Node relay (relay/dist/server.js) with the host service on
/// one side and a phone-shaped client written here on the other. Skipped when
/// the relay has not been built.
private final class StaticHost: MobileHostDelegate, @unchecked Sendable {
    func mobileState() async -> MobileState { MobileState(revision: 3, hostName: "Relay Mac", workspaces: [], sessions: []) }
    func mobileSession(id: String) async -> MobileSessionDetail? { nil }
    func mobileSubmit(sessionId: String, text: String, mode: String?) async throws -> String { "started" }
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

    @Test func phoneReachesTheHostThroughTheRelayWithEndToEndEncryption() async throws {
        guard let script = Self.relayScript, let node = Self.node else { return }
        let port = Int.random(in: 20000...40000)
        let relay = try NativeChildProcess(executable: node, arguments: [script.path], environment: ["PORT": String(port), "HOST": "127.0.0.1", "PATH": "/usr/bin:/bin", "RELAY_ATTACH_TIMEOUT_MS": "5000"],
                                           cwd: script.deletingLastPathComponent(), stdout: { _ in }, stderr: { _ in }, exited: { _ in })
        defer { relay.stop() }
        // Wait for /healthz.
        var healthy = false
        for _ in 0..<50 {
            if let (data, _) = try? await URLSession.shared.data(from: URL(string: "http://127.0.0.1:\(port)/healthz")!), String(decoding: data, as: UTF8.self) == "ok" { healthy = true; break }
            try await Task.sleep(for: .milliseconds(100))
        }
        #expect(healthy)

        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("relay-int-" + UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let service = MobileRemoteService(dataDirectory: directory, hostName: "Relay Mac", appVersion: "1.2.3")
        let host = StaticHost() // the service holds its delegate weakly, as the app store does
        await service.attach(host)
        _ = await service.apply(settings: MobileRemoteSettings(enabled: true, relayURL: "ws://127.0.0.1:\(port)"))
        var status = await service.status()
        for _ in 0..<50 where !status.relayConnected { try await Task.sleep(for: .milliseconds(100)); status = await service.status() }
        #expect(status.relayConnected)
        let offer = try #require(status.pairingURL.flatMap(MobilePairingOffer.parse))
        #expect(offer.relayURL == "ws://127.0.0.1:\(port)" && offer.serverId == status.serverId)

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
        try await sendEncrypted(socket, &cipher, ["type": "auth", "pairingKey": offer.pairingKey, "clientName": "Test phone"])
        let ok = try await receiveEncrypted(socket, &cipher)
        #expect(ok["type"] as? String == "auth_ok" && ok["hostName"] as? String == "Relay Mac" && ok["appVersion"] as? String == "1.2.3")

        try await sendEncrypted(socket, &cipher, ["id": "r1", "method": "GET", "path": "/m1/state?since=0&wait=0"])
        let reply = try await receiveEncrypted(socket, &cipher)
        #expect(reply["id"] as? String == "r1" && reply["status"] as? Int == 200 && (reply["body"] as? [String: Any])?["revision"] as? Int == 3)
        for _ in 0..<50 where await service.status().clients == 0 { try await Task.sleep(for: .milliseconds(50)) }
        #expect(await service.status().clients == 1)

        // Host-initiated notify reaches the phone; ping is answered.
        await service.notify(scope: "state", revision: 4)
        let notify = try await receiveEncrypted(socket, &cipher)
        #expect(notify["type"] as? String == "notify" && notify["scope"] as? String == "state" && notify["revision"] as? Int == 4)
        try await sendEncrypted(socket, &cipher, ["type": "ping"])
        #expect(try await receiveEncrypted(socket, &cipher)["type"] as? String == "pong")
        // A POST with a body and an unknown route travel the same way.
        try await sendEncrypted(socket, &cipher, ["id": "r2", "method": "POST", "path": "/m1/sessions/s1/submit", "body": ["text": "hi"]])
        #expect(try await receiveEncrypted(socket, &cipher)["status"] as? Int == 202)
        try await sendEncrypted(socket, &cipher, ["id": "r3", "method": "GET", "path": "/m1/nothing"])
        #expect(try await receiveEncrypted(socket, &cipher)["status"] as? Int == 404)
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
        await service.shutdown()
        withExtendedLifetime(host) {}
    }
}
