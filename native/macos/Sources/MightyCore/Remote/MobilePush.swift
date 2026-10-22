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

public enum MobilePushKind: String, Codable, Sendable, CaseIterable {
    case completed, failed, permission, question, plan, signin
}

/// The buttons a permission push carries; the phone draws them, the host still decides.
public enum MobilePushButtons: String, Codable, Sendable {
    case claude, antigravity, antigravityOnce, none
}

public struct MobilePushRequest: Codable, Sendable, Equatable {
    public var id: String
    public var runId: String
    public var kind: MobilePushKind
    public var summary: String
    public var buttons: MobilePushButtons
    public init(id: String, runId: String, kind: MobilePushKind, summary: String, buttons: MobilePushButtons) {
        self.id = id; self.runId = runId; self.kind = kind; self.summary = summary; self.buttons = buttons
    }
    /// A pending card as the phone sees it, read as a push (the rule in docs/mobile-remote.md "푸시 알림").
    public init(_ permission: MobilePermission) {
        id = permission.id; runId = permission.runId; summary = permission.summary
        if let card = permission.antigravity {
            kind = .permission
            let ruled = card.actions.filter { $0.rule != nil }
            if !permission.canAllow || ruled.isEmpty { buttons = .none }
            else { buttons = ruled.contains { $0.onceOnly == true } ? .antigravityOnce : .antigravity }
        } else if permission.questionnaire != nil || permission.toolName == "AskUserQuestion" {
            kind = .question; buttons = .none
        } else if permission.plan != nil || permission.toolName == ClaudePlanMode.toolName {
            kind = .plan; buttons = .none
        } else {
            kind = .permission; buttons = permission.canAllow ? .claude : .none
        }
    }
}

/// One pane as the push planner sees it.
public struct MobilePushPane: Codable, Sendable, Equatable {
    public var sessionId: String
    public var workspace: String
    public var title: String
    /// The pane's own run status (`running`, `completed`, `error`, `stopped`, …), not the phone's display status.
    public var status: String
    public var preview: String?
    public var requests: [MobilePushRequest]
    public var signInLost: Bool
    public init(sessionId: String, workspace: String, title: String, status: String, preview: String?, requests: [MobilePushRequest], signInLost: Bool) {
        self.sessionId = sessionId; self.workspace = workspace; self.title = title; self.status = status
        self.preview = preview; self.requests = requests; self.signInLost = signInLost
    }
}

public struct MobilePushPermission: Codable, Sendable, Equatable {
    public var requestId: String
    public var runId: String
    public var buttons: MobilePushButtons
    public init(requestId: String, runId: String, buttons: MobilePushButtons) { self.requestId = requestId; self.runId = runId; self.buttons = buttons }
}

/// The plaintext inside a push envelope.
public struct MobilePushMessage: Codable, Sendable, Equatable {
    public var v = 1
    public var id: String
    public var kind: MobilePushKind
    public var hostId: String
    public var hostName: String
    public var workspace: String
    public var title: String
    public var summary: String
    public var sessionId: String
    public var permission: MobilePushPermission?
    public var at: String
    public init(id: String, kind: MobilePushKind, hostId: String, hostName: String, workspace: String, title: String, summary: String, sessionId: String,
                permission: MobilePushPermission?, at: String) {
        self.id = id; self.kind = kind; self.hostId = hostId; self.hostName = hostName; self.workspace = workspace
        self.title = title; self.summary = summary; self.sessionId = sessionId; self.permission = permission; self.at = at
    }
    /// UTF-8 JSON within `MobilePushSeal.maximumPlaintext`; the summary goes first when it does not fit.
    public func encodedForPush() -> Data? {
        let encoder = JSONEncoder()
        if let data = try? encoder.encode(self), data.count <= MobilePushSeal.maximumPlaintext { return data }
        var shorter = self; shorter.summary = ""
        guard let data = try? encoder.encode(shorter), data.count <= MobilePushSeal.maximumPlaintext else { return nil }
        return data
    }
}

/// Decides what a host pushes from what its panes show now and what they showed last time.
public struct MobilePushPlanner: Sendable {
    public static let sentMemory = 512
    private struct Memory: Sendable {
        var status: String
        var requests: Set<String>
        var signInLost: Bool
        var runs: Int
        var signins: Int
    }
    private var baseline = false
    private var memory: [String: Memory] = [:]
    private var sent: [String] = []
    private var sentSet = Set<String>()

    public init() {}

    public mutating func observe(_ panes: [MobilePushPane], hostId: String, hostName: String, at: String) -> [MobilePushMessage] {
        var messages: [MobilePushMessage] = []
        var next: [String: Memory] = [:]
        for pane in panes {
            let previous = memory[pane.sessionId]
            var record = Memory(status: pane.status, requests: Set(pane.requests.map(Self.requestId)), signInLost: pane.signInLost,
                                runs: previous?.runs ?? 0, signins: previous?.signins ?? 0)
            if baseline {
                for request in pane.requests where previous?.requests.contains(Self.requestId(request)) != true {
                    let permission = request.kind == .permission ? MobilePushPermission(requestId: request.id, runId: request.runId, buttons: request.buttons) : nil
                    emit(&messages, pane: pane, kind: request.kind, id: Self.requestId(request), summary: request.summary, permission: permission, hostId: hostId, hostName: hostName, at: at)
                }
                if previous?.status == "running", pane.requests.isEmpty, pane.status == "completed" || pane.status == "error" {
                    record.runs += 1
                    let kind: MobilePushKind = pane.status == "completed" ? .completed : .failed
                    emit(&messages, pane: pane, kind: kind, id: "run:\(pane.sessionId):\(record.runs):\(kind.rawValue)", summary: pane.preview ?? "", permission: nil, hostId: hostId, hostName: hostName, at: at)
                }
                if pane.signInLost, previous?.signInLost != true {
                    record.signins += 1
                    emit(&messages, pane: pane, kind: .signin, id: "signin:\(pane.sessionId):\(record.signins)", summary: "", permission: nil, hostId: hostId, hostName: hostName, at: at)
                }
            }
            next[pane.sessionId] = record
        }
        memory = next
        baseline = true
        return messages
    }

    private static func requestId(_ request: MobilePushRequest) -> String { "request:\(request.runId):\(request.id)" }

    private mutating func emit(_ messages: inout [MobilePushMessage], pane: MobilePushPane, kind: MobilePushKind, id: String, summary: String,
                               permission: MobilePushPermission?, hostId: String, hostName: String, at: String) {
        guard !sentSet.contains(id) else { return }
        sent.append(id); sentSet.insert(id)
        if sent.count > Self.sentMemory { sentSet.remove(sent.removeFirst()) }
        messages.append(MobilePushMessage(id: id, kind: kind, hostId: hostId, hostName: Self.clip(hostName, 80), workspace: Self.clip(pane.workspace, 80),
                                          title: Self.clip(pane.title, 80), summary: Self.clip(Self.oneLine(summary), 200), sessionId: pane.sessionId,
                                          permission: permission, at: at))
    }

    static func clip(_ text: String, _ limit: Int) -> String {
        var scalars = String.UnicodeScalarView()
        scalars.append(contentsOf: text.unicodeScalars.prefix(limit))
        return String(scalars)
    }

    static func oneLine(_ text: String) -> String {
        text.replacingOccurrences(of: "\r\n", with: " ").replacingOccurrences(of: "\n", with: " ").replacingOccurrences(of: "\r", with: " ")
            .trimmingCharacters(in: .whitespaces)
    }
}

public struct MobilePushDevice: Sendable, Equatable {
    public var deviceId: String
    public var handle: String
    public var tokenHash: String
    public var kinds: Set<MobilePushKind>
    public init(deviceId: String, handle: String, tokenHash: String, kinds: Set<MobilePushKind>) {
        self.deviceId = deviceId; self.handle = handle; self.tokenHash = tokenHash; self.kinds = kinds
    }
}

/// "This phone has this session on screen", until `until`.
public struct MobilePushViewing: Sendable, Equatable {
    public var sessionId: String
    public var until: Date
    public init(sessionId: String, until: Date) { self.sessionId = sessionId; self.until = until }
}

public enum MobilePushRecipients {
    public static let viewingWindow: TimeInterval = 45
    public static func targets(for message: MobilePushMessage, devices: [MobilePushDevice], viewing: [String: MobilePushViewing], now: Date) -> [MobilePushDevice] {
        devices.filter { device in
            guard device.kinds.contains(message.kind) else { return false }
            if let seen = viewing[device.deviceId], seen.sessionId == message.sessionId, seen.until > now { return false }
            return true
        }
    }
}
