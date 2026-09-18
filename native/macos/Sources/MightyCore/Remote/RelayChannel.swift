import CryptoKit
import Foundation

/// End-to-end encryption for the relay path (docs/relay.md). The relay only
/// ever sees ciphertext: X25519 agreement, HKDF-SHA256 key derivation and
/// ChaCha20-Poly1305 frames whose nonces carry a direction byte and a
/// strictly increasing counter, so replayed or reordered frames are rejected.
public enum RelayCrypto {
    public static let info = Data("mightyclaude-relay-v1".utf8)
    public static let clientDirection: UInt8 = 0x01
    public static let hostDirection: UInt8 = 0x02
    public static let nonceLength = 12
    public static let tagLength = 16

    public static func randomBytes(_ count: Int) -> Data {
        var bytes = [UInt8](repeating: 0, count: count)
        _ = SecRandomCopyBytes(kSecRandomDefault, count, &bytes)
        return Data(bytes)
    }

    /// Derives the session key from our private key and the peer's public key.
    static func sessionKey(privateKey: Curve25519.KeyAgreement.PrivateKey, peerPublicKey: Data, clientNonce: Data, serverNonce: Data) throws -> SymmetricKey {
        guard peerPublicKey.count == 32, clientNonce.count == 16, serverNonce.count == 16 else { throw MightyError("핸드셰이크 값의 길이가 올바르지 않습니다.") }
        let peer = try Curve25519.KeyAgreement.PublicKey(rawRepresentation: peerPublicKey)
        let shared = try privateKey.sharedSecretFromKeyAgreement(with: peer)
        let allZero = shared.withUnsafeBytes { buffer in buffer.allSatisfy { $0 == 0 } }
        guard !allZero else { throw MightyError("공유 비밀이 비어 있어 연결을 거부합니다.") }
        return shared.hkdfDerivedSymmetricKey(using: SHA256.self, salt: clientNonce + serverNonce, sharedInfo: info, outputByteCount: 32)
    }
}

/// One direction-aware cipher per connection. `seal` and `open` must be
/// called from a single isolation domain (the owning actor) because counters
/// advance on every call.
public struct RelayCipher: Sendable {
    private let key: SymmetricKey
    private let sendDirection: UInt8
    private let receiveDirection: UInt8
    private var sendCounter: UInt64 = 0
    private var lastReceived: Int64 = -1

    public init(privateKey: Curve25519.KeyAgreement.PrivateKey, peerPublicKey: Data, clientNonce: Data, serverNonce: Data, isHost: Bool) throws {
        key = try RelayCrypto.sessionKey(privateKey: privateKey, peerPublicKey: peerPublicKey, clientNonce: clientNonce, serverNonce: serverNonce)
        sendDirection = isHost ? RelayCrypto.hostDirection : RelayCrypto.clientDirection
        receiveDirection = isHost ? RelayCrypto.clientDirection : RelayCrypto.hostDirection
    }

    static func nonce(direction: UInt8, counter: UInt64) -> Data {
        var bytes = Data([direction, 0, 0, 0])
        for shift in stride(from: 56, through: 0, by: -8) { bytes.append(UInt8((counter >> UInt64(shift)) & 0xff)) }
        return bytes
    }

    /// `[12B nonce][ciphertext][16B tag]`, the same layout CryptoKit calls `combined`.
    public mutating func seal(_ plaintext: Data) throws -> Data {
        guard sendCounter < UInt64.max else { throw MightyError("암호화 카운터가 소진되었습니다.") }
        let nonce = try ChaChaPoly.Nonce(data: Self.nonce(direction: sendDirection, counter: sendCounter))
        sendCounter += 1
        return try ChaChaPoly.seal(plaintext, using: key, nonce: nonce).combined
    }

    public mutating func open(_ frame: Data) throws -> Data {
        guard frame.count >= RelayCrypto.nonceLength + RelayCrypto.tagLength else { throw MightyError("암호화 프레임이 너무 짧습니다.") }
        let nonce = frame.prefix(RelayCrypto.nonceLength)
        guard nonce[nonce.startIndex] == receiveDirection, nonce[nonce.startIndex + 1] == 0, nonce[nonce.startIndex + 2] == 0, nonce[nonce.startIndex + 3] == 0 else {
            throw MightyError("프레임 방향이 올바르지 않습니다.")
        }
        var counter: UInt64 = 0
        for byte in nonce.dropFirst(4) { counter = (counter << 8) | UInt64(byte) }
        guard counter <= UInt64(Int64.max), Int64(counter) > lastReceived else { throw MightyError("재전송되었거나 순서가 바뀐 프레임입니다.") }
        let plaintext = try ChaChaPoly.open(ChaChaPoly.SealedBox(combined: frame), using: key)
        lastReceived = Int64(counter)
        return plaintext
    }
}

/// The host's long-lived X25519 identity, kept next to the pairing key.
public struct RelayKeypair: Sendable {
    public let privateKey: Curve25519.KeyAgreement.PrivateKey
    public var publicKeyData: Data { privateKey.publicKey.rawRepresentation }
    public var publicKeyB64: String { publicKeyData.base64EncodedString() }

    public init() { privateKey = Curve25519.KeyAgreement.PrivateKey() }
    public init(privateKey: Curve25519.KeyAgreement.PrivateKey) { self.privateKey = privateKey }

    /// Loads `relay-keypair.json` or creates it owner-only. A damaged file is
    /// replaced rather than trusted.
    public static func load(from url: URL) throws -> RelayKeypair {
        if let data = try? Data(contentsOf: url), let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
           object["v"] as? Int == 1, let secret = (object["secretKeyB64"] as? String).flatMap({ Data(base64Encoded: $0) }),
           let key = try? Curve25519.KeyAgreement.PrivateKey(rawRepresentation: secret) {
            try? FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
            return RelayKeypair(privateKey: key)
        }
        let fresh = RelayKeypair()
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        let payload = try JSONSerialization.data(withJSONObject: ["v": 1, "publicKeyB64": fresh.publicKeyB64, "secretKeyB64": fresh.privateKey.rawRepresentation.base64EncodedString()], options: [.sortedKeys])
        let temporary = url.deletingLastPathComponent().appendingPathComponent(url.lastPathComponent + "." + UUID().uuidString)
        guard FileManager.default.createFile(atPath: temporary.path, contents: payload, attributes: [.posixPermissions: 0o600]) else { throw MightyError("릴레이 키쌍을 저장하지 못했습니다.") }
        if FileManager.default.fileExists(atPath: url.path) { _ = try FileManager.default.replaceItemAt(url, withItemAt: temporary) }
        else { try FileManager.default.moveItem(at: temporary, to: url) }
        return fresh
    }
}

/// What the QR carries: enough for a phone to find the relay, prove it is
/// talking to this Mac (public key) and be admitted (pairing key).
public struct MobilePairingOffer: Sendable, Equatable {
    public static let version = 2
    public var serverId: String
    public var publicKeyB64: String
    public var relayURL: String
    public var pairingKey: String
    public var name: String

    public init(serverId: String, publicKeyB64: String, relayURL: String, pairingKey: String, name: String) {
        self.serverId = serverId; self.publicKeyB64 = publicKeyB64; self.relayURL = relayURL; self.pairingKey = pairingKey; self.name = name
    }

    public var url: String {
        var components = URLComponents()
        components.scheme = MobilePairing.scheme; components.host = "pair"
        components.queryItems = [.init(name: "v", value: String(Self.version)), .init(name: "sid", value: serverId), .init(name: "pk", value: Self.base64url(publicKeyB64)),
                                 .init(name: "relay", value: relayURL), .init(name: "key", value: pairingKey), .init(name: "name", value: String(name.prefix(120)))]
        return components.string ?? ""
    }

    public static func parse(_ text: String) -> MobilePairingOffer? {
        guard let components = URLComponents(string: text.trimmingCharacters(in: .whitespacesAndNewlines)), components.scheme == MobilePairing.scheme, components.host == "pair" else { return nil }
        var values: [String: String] = [:]
        for item in components.queryItems ?? [] { values[item.name] = item.value }
        guard values["v"] == String(version), let sid = values["sid"], CoreValidation.identifier(sid), let pk = values["pk"].flatMap(fromBase64url), Data(base64Encoded: pk)?.count == 32,
              let relay = values["relay"], RelayEndpoint.normalize(relay) != nil, let key = values["key"], RemoteValidation.token(key) else { return nil }
        return MobilePairingOffer(serverId: sid, publicKeyB64: pk, relayURL: relay, pairingKey: key, name: values["name"] ?? "")
    }

    static func base64url(_ base64: String) -> String { base64.replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_").replacingOccurrences(of: "=", with: "") }
    static func fromBase64url(_ value: String) -> String? {
        var base64 = value.replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
        while base64.count % 4 != 0 { base64 += "=" }
        return Data(base64Encoded: base64) == nil ? nil : base64
    }
}

public enum RelayEndpoint {
    /// Accepts `wss://host[:port]`, `ws://host[:port]`, or a bare `host:port`
    /// (treated as TLS). Returns the origin without path or query.
    public static func normalize(_ input: String) -> String? {
        var text = input.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty, text.count <= 256, !text.contains(" ") else { return nil }
        if !text.contains("://") { text = "wss://" + text }
        text = text.replacingOccurrences(of: "https://", with: "wss://").replacingOccurrences(of: "http://", with: "ws://")
        guard let url = URL(string: text), let scheme = url.scheme, ["ws", "wss"].contains(scheme), let host = url.host, !host.isEmpty else { return nil }
        var origin = scheme + "://" + host
        if let port = url.port { origin += ":" + String(port) }
        return origin
    }

    static func socketURL(relay: String, serverId: String, role: String, connectionId: String?) -> URL? {
        guard let origin = normalize(relay), var components = URLComponents(string: origin + "/ws") else { return nil }
        var items = [URLQueryItem(name: "serverId", value: serverId), URLQueryItem(name: "role", value: role), URLQueryItem(name: "v", value: "1")]
        if let connectionId { items.append(URLQueryItem(name: "connectionId", value: connectionId)) }
        components.queryItems = items
        return components.url
    }
}
