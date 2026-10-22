import CryptoKit
import Foundation

/// The seal on a push (docs/mobile-remote.md "푸시 알림"): only the phone that holds the
/// device token can open it. The relay and FCM carry it blind.
///
/// key = HKDF-SHA256(ikm: SHA-256(UTF-8 deviceToken), salt: UTF-8 clientId, info: "mighty-push-v1", 32 bytes).
/// The host stores exactly that SHA-256 as `MobileDevice.tokenHash`, so it needs no new secret;
/// a phone on the relay and one on the direct path authenticate with the same token, so both get the same key.
/// envelope = base64url(0x01 ‖ nonce(12) ‖ ChaCha20-Poly1305(plaintext, aad: "mighty-push-v1:" + handle) ‖ tag(16)).
public enum MobilePushSeal {
    public static let info = "mighty-push-v1"
    public static let version: UInt8 = 1
    public static let maximumPlaintext = 2_048
    public static let maximumEnvelope = 3_072

    public static func key(tokenHash: String, clientId: String) -> SymmetricKey? {
        guard tokenHash.utf8.count == 64, let ikm = hexBytes(tokenHash) else { return nil }
        return HKDF<SHA256>.deriveKey(inputKeyMaterial: SymmetricKey(data: ikm), salt: Data(clientId.utf8), info: Data(info.utf8), outputByteCount: 32)
    }

    public static func seal(_ plaintext: Data, key: SymmetricKey, handle: String, nonce: ChaChaPoly.Nonce = ChaChaPoly.Nonce()) throws -> String {
        guard plaintext.count <= maximumPlaintext else { throw MightyError("push message too long") }
        let box = try ChaChaPoly.seal(plaintext, using: key, nonce: nonce, authenticating: aad(handle))
        return base64URL(Data([version]) + box.combined)
    }

    public static func open(_ envelope: String, key: SymmetricKey, handle: String) -> Data? {
        guard envelope.utf8.count <= maximumEnvelope, let bytes = fromBase64URL(envelope), bytes.count >= 1 + 12 + 16, bytes.first == version,
              let box = try? ChaChaPoly.SealedBox(combined: Data(bytes.dropFirst())) else { return nil }
        return try? ChaChaPoly.open(box, using: key, authenticating: aad(handle))
    }

    static func aad(_ handle: String) -> Data { Data((info + ":" + handle).utf8) }

    /// Lowercase or uppercase ASCII hex only (`UInt8(_:radix:)` alone would take a sign such as "+f").
    static func hexBytes(_ text: String) -> Data? {
        let digits = Array(text.utf8)
        guard digits.count % 2 == 0 else { return nil }
        func value(_ digit: UInt8) -> UInt8? {
            switch digit {
            case UInt8(ascii: "0")...UInt8(ascii: "9"): digit - UInt8(ascii: "0")
            case UInt8(ascii: "a")...UInt8(ascii: "f"): digit - UInt8(ascii: "a") + 10
            case UInt8(ascii: "A")...UInt8(ascii: "F"): digit - UInt8(ascii: "A") + 10
            default: nil
            }
        }
        var bytes = Data(capacity: digits.count / 2)
        for index in stride(from: 0, to: digits.count, by: 2) {
            guard let high = value(digits[index]), let low = value(digits[index + 1]) else { return nil }
            bytes.append(high << 4 | low)
        }
        return bytes
    }

    static func base64URL(_ data: Data) -> String {
        data.base64EncodedString().replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_").replacingOccurrences(of: "=", with: "")
    }

    static func fromBase64URL(_ text: String) -> Data? {
        guard text.range(of: "^[A-Za-z0-9_-]*$", options: .regularExpression) != nil else { return nil }
        var standard = text.replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
        while standard.count % 4 != 0 { standard += "=" }
        return Data(base64Encoded: standard)
    }
}
