import CryptoKit
import Foundation
import Testing
@testable import MightyCore

struct RelayChannelTests {
    @Test func bothSidesDeriveTheSameKeyAndCountersRejectReplayAndReordering() throws {
        let host = RelayKeypair(), client = Curve25519.KeyAgreement.PrivateKey()
        let clientNonce = RelayCrypto.randomBytes(16), serverNonce = RelayCrypto.randomBytes(16)
        var hostCipher = try RelayCipher(privateKey: host.privateKey, peerPublicKey: client.publicKey.rawRepresentation, clientNonce: clientNonce, serverNonce: serverNonce, isHost: true)
        var clientCipher = try RelayCipher(privateKey: client, peerPublicKey: host.publicKeyData, clientNonce: clientNonce, serverNonce: serverNonce, isHost: false)
        let first = try clientCipher.seal(Data("one".utf8)), second = try clientCipher.seal(Data("two".utf8))
        #expect(first.count == 12 + 3 + 16)
        #expect(Array(first.prefix(4)) == [0x01, 0, 0, 0] && Array(first[4..<12]) == [0, 0, 0, 0, 0, 0, 0, 0])
        #expect(Array(second[4..<12]) == [0, 0, 0, 0, 0, 0, 0, 1])
        #expect(try hostCipher.open(first) == Data("one".utf8))
        #expect(throws: (any Error).self) { _ = try hostCipher.open(first) }      // replay
        #expect(try hostCipher.open(second) == Data("two".utf8))
        let third = try clientCipher.seal(Data("three".utf8)), fourth = try clientCipher.seal(Data("four".utf8))
        #expect(try hostCipher.open(fourth) == Data("four".utf8))
        #expect(throws: (any Error).self) { _ = try hostCipher.open(third) }      // reordered
        // Direction is enforced: a host frame cannot be fed back to the host.
        let reply = try hostCipher.seal(Data("{\"type\":\"auth_ok\"}".utf8))
        #expect(Array(reply.prefix(1)) == [0x02])
        #expect(throws: (any Error).self) { _ = try hostCipher.open(reply) }
        #expect(try clientCipher.open(reply) == Data("{\"type\":\"auth_ok\"}".utf8))
        // Tampering fails authentication.
        var tampered = try clientCipher.seal(Data("five".utf8)); tampered[tampered.count - 1] ^= 0x01
        #expect(throws: (any Error).self) { _ = try hostCipher.open(tampered) }
        // Wrong nonce lengths are refused.
        #expect(throws: (any Error).self) { _ = try RelayCipher(privateKey: client, peerPublicKey: host.publicKeyData, clientNonce: Data([1, 2]), serverNonce: serverNonce, isHost: false) }
    }

    @Test func keypairPersistsOwnerOnlyAndPairingOfferRoundTrips() throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("relay-" + UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let url = directory.appendingPathComponent("relay-keypair.json")
        let first = try RelayKeypair.load(from: url), second = try RelayKeypair.load(from: url)
        #expect(first.publicKeyB64 == second.publicKeyB64)
        #expect((try FileManager.default.attributesOfItem(atPath: url.path)[.posixPermissions] as? Int) == 0o600)
        try Data("garbage".utf8).write(to: url)
        #expect(try RelayKeypair.load(from: url).publicKeyB64 != first.publicKeyB64)

        let key = MobilePairing.generateKey()!
        let offer = MobilePairingOffer(serverId: "host-1", publicKeyB64: first.publicKeyB64, relayURL: "wss://relay.example.com:8443", pairingKey: key, name: "Young의 Mac")
        #expect(offer.url.hasPrefix("mightyclaude://pair?v=2&sid=host-1&pk="))
        #expect(!offer.url.contains("+") && !offer.url.contains("/ws"))
        #expect(MobilePairingOffer.parse(offer.url) == offer)
        #expect(MobilePairingOffer.parse("mightyclaude://pair?v=1&host=100.64.0.1&port=43138&key=" + key) == nil)
        #expect(MobilePairingOffer.parse(offer.url.replacingOccurrences(of: "wss://relay.example.com:8443", with: "ftp://x")) == nil)
    }

    @Test func relayEndpointsNormalizeAndBuildSocketURLs() {
        #expect(RelayEndpoint.normalize("relay.example.com") == "wss://relay.example.com")
        #expect(RelayEndpoint.normalize("http://192.168.0.5:8787/ws?x=1") == "ws://192.168.0.5:8787")
        #expect(RelayEndpoint.normalize("wss://relay.example.com/") == "wss://relay.example.com")
        #expect(RelayEndpoint.normalize("") == nil && RelayEndpoint.normalize("not a url") == nil)
        let url = RelayEndpoint.socketURL(relay: "ws://127.0.0.1:8787", serverId: "abc", role: "server", connectionId: "c-1234567")
        #expect(url?.absoluteString == "ws://127.0.0.1:8787/ws?serverId=abc&role=server&v=1&connectionId=c-1234567")
        #expect(MobileRemoteService.validConnectionId("c-1234567") && !MobileRemoteService.validConnectionId("bad id"))
        #expect(MobileRemoteSettings(enabled: true, relayURL: "relay.example.com").normalized.relayURL == "wss://relay.example.com")
    }
}
