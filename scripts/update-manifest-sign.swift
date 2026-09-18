// Ed25519 helper for the update manifest. Compiled on demand by
// scripts/make-update-manifest.py (swiftc, no Xcode project needed).
//
//   update-manifest-sign keygen <private-key-file>      → prints the base64 public key
//   update-manifest-sign public <private-key-file>      → prints the base64 public key
//   update-manifest-sign sign <private-key-file> <file> → prints the base64 signature of the file bytes
//   update-manifest-sign verify <base64-public-key> <file> <base64-signature>
//
// The private key file holds the raw 32-byte seed, base64, one line (0600).
import CryptoKit
import Foundation

func fail(_ message: String) -> Never { FileHandle.standardError.write(Data((message + "\n").utf8)); exit(1) }
func readKey(_ path: String) -> Curve25519.Signing.PrivateKey {
    guard let text = try? String(contentsOfFile: path, encoding: .utf8), let raw = Data(base64Encoded: text.trimmingCharacters(in: .whitespacesAndNewlines)),
          let key = try? Curve25519.Signing.PrivateKey(rawRepresentation: raw) else { fail("개인 키 파일을 읽지 못했습니다: \(path)") }
    return key
}
let arguments = Array(CommandLine.arguments.dropFirst())
guard let command = arguments.first else { fail("usage: keygen|public|sign|verify …") }
switch command {
case "keygen":
    guard arguments.count == 2 else { fail("usage: keygen <private-key-file>") }
    let key = Curve25519.Signing.PrivateKey()
    FileManager.default.createFile(atPath: arguments[1], contents: Data((key.rawRepresentation.base64EncodedString() + "\n").utf8), attributes: [.posixPermissions: 0o600])
    print(key.publicKey.rawRepresentation.base64EncodedString())
case "public":
    guard arguments.count == 2 else { fail("usage: public <private-key-file>") }
    print(readKey(arguments[1]).publicKey.rawRepresentation.base64EncodedString())
case "sign":
    guard arguments.count == 3, let data = FileManager.default.contents(atPath: arguments[2]) else { fail("usage: sign <private-key-file> <file>") }
    guard let signature = try? readKey(arguments[1]).signature(for: data) else { fail("서명하지 못했습니다.") }
    print(signature.base64EncodedString())
case "verify":
    guard arguments.count == 4, let raw = Data(base64Encoded: arguments[1]), let key = try? Curve25519.Signing.PublicKey(rawRepresentation: raw),
          let data = FileManager.default.contents(atPath: arguments[2]), let signature = Data(base64Encoded: arguments[3]) else { fail("usage: verify <base64-public-key> <file> <base64-signature>") }
    print(key.isValidSignature(signature, for: data) ? "valid" : "INVALID"); exit(key.isValidSignature(signature, for: data) ? 0 : 2)
default: fail("unknown command \(command)")
}
