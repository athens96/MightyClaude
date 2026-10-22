import CryptoKit
import Foundation
import Testing
@testable import MightyCore

/// `native/contracts/fixtures/push.json`, read by the Mac, Windows (`MobilePushVerification`) and the phone (`push-seal.test.ts`).
enum PushFixture {
    static let url: URL = {
        var url = URL(fileURLWithPath: #filePath)
        for _ in 0..<5 { url.deleteLastPathComponent() }
        return url.appendingPathComponent("native/contracts/fixtures/push.json")
    }()
    struct Seal: Decodable {
        let deviceToken, clientId, handle, tokenHash, keyB64, nonceB64, aad, plaintext, envelope, otherHandle: String
        let tampered: [String]
    }
    struct File: Decodable {
        let seal: Seal
    }
    static func load() throws -> File { try JSONDecoder().decode(File.self, from: Data(contentsOf: url)) }
}

struct MobilePushSealTests {
    @Test func theKeyComesFromTheTokenHashAndTheClientId() throws {
        let seal = try PushFixture.load().seal
        let key = try #require(MobilePushSeal.key(tokenHash: seal.tokenHash, clientId: seal.clientId))
        #expect(key.withUnsafeBytes { Data($0) }.base64EncodedString() == seal.keyB64)
        #expect(MobileDeviceRegistry.hash(seal.deviceToken) == seal.tokenHash)
        #expect(MobilePushSeal.key(tokenHash: "not hex", clientId: seal.clientId) == nil)
        #expect(MobilePushSeal.key(tokenHash: "+f" + seal.tokenHash.dropFirst(2), clientId: seal.clientId) == nil)
    }

    @Test func sealingWithTheFixtureNonceGivesTheFixtureEnvelope() throws {
        let seal = try PushFixture.load().seal
        let key = try #require(MobilePushSeal.key(tokenHash: seal.tokenHash, clientId: seal.clientId))
        let nonce = try ChaChaPoly.Nonce(data: try #require(Data(base64Encoded: seal.nonceB64)))
        #expect(try MobilePushSeal.seal(Data(seal.plaintext.utf8), key: key, handle: seal.handle, nonce: nonce) == seal.envelope)
    }

    @Test func openingChecksTheTagTheVersionAndTheHandle() throws {
        let seal = try PushFixture.load().seal
        let key = try #require(MobilePushSeal.key(tokenHash: seal.tokenHash, clientId: seal.clientId))
        #expect(MobilePushSeal.open(seal.envelope, key: key, handle: seal.handle) == Data(seal.plaintext.utf8))
        for tampered in seal.tampered { #expect(MobilePushSeal.open(tampered, key: key, handle: seal.handle) == nil) }
        #expect(MobilePushSeal.open(seal.envelope, key: key, handle: seal.otherHandle) == nil)
        #expect(MobilePushSeal.open(String(repeating: "A", count: 3_073), key: key, handle: seal.handle) == nil)
    }

    @Test func aMessageOverTheLimitIsNotSealed() throws {
        let seal = try PushFixture.load().seal
        let key = try #require(MobilePushSeal.key(tokenHash: seal.tokenHash, clientId: seal.clientId))
        #expect(throws: MightyError.self) { try MobilePushSeal.seal(Data(count: 2_049), key: key, handle: seal.handle) }
        let longest = try MobilePushSeal.seal(Data(count: 2_048), key: key, handle: seal.handle)
        #expect(longest.count <= MobilePushSeal.maximumEnvelope)
    }
}
