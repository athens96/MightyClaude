import CryptoKit
import Foundation
import Testing
@testable import MightyCore

/// Fixed-key vectors shared with the Expo client (mobile/src/__tests__/relay-interop.test.ts).
/// Both sides derive the same key from these inputs and produce byte-identical
/// frames, because the nonce is a counter and ChaCha20-Poly1305 is deterministic.
struct RelayInteropVectorTests {
    static let fixture: URL = {
        var url = URL(fileURLWithPath: #filePath)
        for _ in 0..<5 { url.deleteLastPathComponent() }
        return url.appendingPathComponent("native/contracts/relay-vectors.json")
    }()
    static let hostSecret = Data((1...32).map { UInt8($0) })
    static let clientSecret = Data((101...132).map { UInt8($0) })
    static let clientNonce = Data((0..<16).map { UInt8($0 * 3) })
    static let serverNonce = Data((0..<16).map { UInt8(200 - $0) })

    @Test func vectorsMatchTheCommittedFixture() throws {
        let host = try Curve25519.KeyAgreement.PrivateKey(rawRepresentation: Self.hostSecret)
        let client = try Curve25519.KeyAgreement.PrivateKey(rawRepresentation: Self.clientSecret)
        let key = try RelayCrypto.sessionKey(privateKey: host, peerPublicKey: client.publicKey.rawRepresentation, clientNonce: Self.clientNonce, serverNonce: Self.serverNonce)
        let keyData = key.withUnsafeBytes { Data($0) }
        var hostCipher = try RelayCipher(privateKey: host, peerPublicKey: client.publicKey.rawRepresentation, clientNonce: Self.clientNonce, serverNonce: Self.serverNonce, isHost: true)
        var clientCipher = try RelayCipher(privateKey: client, peerPublicKey: host.publicKey.rawRepresentation, clientNonce: Self.clientNonce, serverNonce: Self.serverNonce, isHost: false)
        let hostFrames = [try hostCipher.seal(Data(#"{"type":"auth_ok","hostName":"Vector Mac"}"#.utf8)), try hostCipher.seal(Data(#"{"type":"notify","scope":"state","revision":7}"#.utf8))]
        let clientFrames = [try clientCipher.seal(Data(#"{"type":"auth","pairingKey":"vector-key"}"#.utf8)), try clientCipher.seal(Data(#"{"id":"r1","method":"GET","path":"/m1/info"}"#.utf8))]
        let generated: [String: Any] = [
            "v": 1,
            "hostSecretKeyB64": Self.hostSecret.base64EncodedString(), "hostPublicKeyB64": host.publicKey.rawRepresentation.base64EncodedString(),
            "clientSecretKeyB64": Self.clientSecret.base64EncodedString(), "clientPublicKeyB64": client.publicKey.rawRepresentation.base64EncodedString(),
            "clientNonceB64": Self.clientNonce.base64EncodedString(), "serverNonceB64": Self.serverNonce.base64EncodedString(),
            "sessionKeyB64": keyData.base64EncodedString(),
            "hostToClientFramesB64": hostFrames.map { $0.base64EncodedString() },
            "hostToClientPlaintexts": [#"{"type":"auth_ok","hostName":"Vector Mac"}"#, #"{"type":"notify","scope":"state","revision":7}"#],
            "clientToHostFramesB64": clientFrames.map { $0.base64EncodedString() },
            "clientToHostPlaintexts": [#"{"type":"auth","pairingKey":"vector-key"}"#, #"{"id":"r1","method":"GET","path":"/m1/info"}"#],
        ]
        let data = try JSONSerialization.data(withJSONObject: generated, options: [.prettyPrinted, .sortedKeys])
        if let existing = try? Data(contentsOf: Self.fixture), let saved = try? JSONSerialization.jsonObject(with: existing) as? [String: Any] {
            // The committed vectors are the contract; a change here must be deliberate.
            #expect(saved["sessionKeyB64"] as? String == keyData.base64EncodedString())
            #expect(saved["hostToClientFramesB64"] as? [String] == hostFrames.map { $0.base64EncodedString() })
            #expect(saved["clientToHostFramesB64"] as? [String] == clientFrames.map { $0.base64EncodedString() })
        } else {
            try data.write(to: Self.fixture)
        }
        // And the frames open on the other side.
        #expect(try clientCipher.open(hostFrames[0]) == Data(#"{"type":"auth_ok","hostName":"Vector Mac"}"#.utf8))
        #expect(try hostCipher.open(clientFrames[0]) == Data(#"{"type":"auth","pairingKey":"vector-key"}"#.utf8))
    }
}
