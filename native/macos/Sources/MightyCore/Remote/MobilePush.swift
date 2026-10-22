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

extension MobilePushPane {
    /// The longest preview handed to the planner (it clips again for the message).
    public static let previewLimit = 400

    /// The panes as the planner sees them, from what the app holds: only the panes the phone can see
    /// (no files panes, no other computer's workspaces); the pending requests minus the ones a style
    /// is answering on its own (`autoAllowed`), then the pane's Antigravity card; a confirmed lost
    /// sign-in (`login`, never a raw auth failure); and the raw run status, so a pane waiting on a
    /// permission is still "running".
    public static func project(sessions: [RunSession], workspaces: [Workspace], permissions: [String: [ToolPermissionRequest]],
                               autoAllowed: (_ sessionId: String, _ request: ToolPermissionRequest) -> Bool,
                               antigravity: AntigravityPermissionBoard, login: [String: String]) -> [MobilePushPane] {
        let names = Dictionary(workspaces.map { ($0.id, $0.name) }, uniquingKeysWith: { first, _ in first })
        return FilePaneKind.phoneVisible(RemoteWorkspacePhone.sessions(sessions, workspaces: workspaces)).map { session in
            var requests = (permissions[session.id] ?? [])
                .filter { $0.state == "pending" && !autoAllowed(session.id, $0) }
                .map { MobilePushRequest(MobilePermission(request: $0)) }
            if let card = antigravity[session.id] { requests.append(MobilePushRequest(MobilePermission(antigravity: card))) }
            return MobilePushPane(sessionId: session.id, workspace: names[session.workspaceId] ?? "", title: session.title, status: session.status,
                                  preview: MobileRemoteSupport.previewLine(session).map { String($0.text.prefix(previewLimit)) },
                                  requests: requests, signInLost: login[session.id] != nil)
        }
    }
}

extension MobileRemoteSupport {
    /// The line a pane's preview shows on the phone and in a push: the newest entry with text that is not a tool activity.
    public static func previewLine(_ session: RunSession) -> LogEntry? {
        session.logs.last(where: { $0.activity == nil && !$0.text.isEmpty })
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
    /// nil means it does not fit even without the summary: skip this push and log its `id` only.
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
    /// How many sessions' run and sign-in numbers are kept, absent panes oldest first out.
    public static let countMemory = 512
    private struct Memory: Sendable {
        var status: String
        var requests: Set<String>
        var signInLost: Bool
    }
    /// Kept across observations a pane misses, so its ids keep counting up instead of repeating sent ones.
    private struct Count: Sendable {
        var runs = 0
        var signins = 0
        var seen = 0
    }
    private var baseline = false
    private var memory: [String: Memory] = [:]
    private var counts: [String: Count] = [:]
    private var observation = 0
    private var sent: [String] = []
    private var sentSet = Set<String>()

    public init() {}

    /// Starts over from the next observation, which only sets the baseline: what changed while
    /// nothing was watched is not pushed. Run and sign-in numbers and the sent ids are kept, so an
    /// id already pushed is never reused.
    public mutating func reset() {
        baseline = false; memory = [:]
    }

    public mutating func observe(_ panes: [MobilePushPane], hostId: String, hostName: String, at: String) -> [MobilePushMessage] {
        var messages: [MobilePushMessage] = []
        var next: [String: Memory] = [:]
        observation += 1
        for pane in panes {
            let previous = memory[pane.sessionId]
            let record = Memory(status: pane.status, requests: Set(pane.requests.map(Self.requestId)), signInLost: pane.signInLost)
            var count = counts[pane.sessionId] ?? Count()
            count.seen = observation
            if baseline {
                for request in pane.requests where previous?.requests.contains(Self.requestId(request)) != true {
                    let permission = request.kind == .permission ? MobilePushPermission(requestId: request.id, runId: request.runId, buttons: request.buttons) : nil
                    emit(&messages, pane: pane, kind: request.kind, id: Self.requestId(request), summary: request.summary, permission: permission, hostId: hostId, hostName: hostName, at: at)
                }
                if previous?.status == "running", pane.requests.isEmpty, pane.status == "completed" || pane.status == "error" {
                    count.runs += 1
                    let kind: MobilePushKind = pane.status == "completed" ? .completed : .failed
                    emit(&messages, pane: pane, kind: kind, id: "run:\(pane.sessionId):\(count.runs):\(kind.rawValue)", summary: pane.preview ?? "", permission: nil, hostId: hostId, hostName: hostName, at: at)
                }
                if pane.signInLost, previous?.signInLost != true {
                    count.signins += 1
                    emit(&messages, pane: pane, kind: .signin, id: "signin:\(pane.sessionId):\(count.signins)", summary: "", permission: nil, hostId: hostId, hostName: hostName, at: at)
                }
            }
            next[pane.sessionId] = record
            counts[pane.sessionId] = count
        }
        if counts.count > Self.countMemory {
            let absent = counts.filter { $0.value.seen != observation }.sorted { $0.value.seen < $1.value.seen }
            for (sessionId, _) in absent.prefix(counts.count - Self.countMemory) { counts[sessionId] = nil }
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

/// Printing shows only ids and kinds: never the handle, the token hash or what the notification says.
extension MobilePushDevice: CustomStringConvertible, CustomDebugStringConvertible {
    public var description: String { "MobilePushDevice(deviceId: \(deviceId), kinds: \(kinds.map(\.rawValue).sorted()))" }
    public var debugDescription: String { description }
}

extension MobilePushMessage: CustomStringConvertible, CustomDebugStringConvertible {
    public var description: String { "MobilePushMessage(id: \(id), kind: \(kind.rawValue), sessionId: \(sessionId))" }
    public var debugDescription: String { description }
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

public enum MobilePushWire {
    /// A relay push handle: 32 random bytes, base64url without padding.
    public static func validHandle(_ value: String) -> Bool { value.range(of: "^[A-Za-z0-9_-]{43}$", options: .regularExpression) != nil }
    /// The kinds this host knows, in its own order; a newer phone's unknown names are left out, not refused.
    public static func kinds(_ values: [String]) -> [MobilePushKind] { MobilePushKind.allCases.filter { values.contains($0.rawValue) } }
}

/// `POST /m1/push/register`.
public struct MobilePushRegisterRequest: Codable, Sendable {
    public var handle: String
    public var kinds: [String]
}

/// `POST /m1/push/viewing`: the session on the phone's screen, or null when none.
public struct MobilePushViewingRequest: Codable, Sendable {
    public var sessionId: String?
}

public enum MobilePushRelay {
    /// The relay's push route next to the WebSocket the host already dials.
    public static func sendURL(_ relay: String) -> URL? {
        guard var parts = URLComponents(string: relay), let scheme = parts.scheme?.lowercased(), parts.host?.isEmpty == false else { return nil }
        switch scheme {
        case "wss", "https": parts.scheme = "https"
        case "ws", "http": parts.scheme = "http"
        default: return nil
        }
        parts.path = "/push/v1/send"; parts.query = nil; parts.fragment = nil
        return parts.url
    }
}

/// `POST /push/v1/send` on the relay: the payload is the sealed envelope, never plaintext.
public struct MobilePushSendRequest: Codable, Sendable, Equatable {
    public var serverId: String
    public var handle: String
    public var payload: String
    public init(serverId: String, handle: String, payload: String) { self.serverId = serverId; self.handle = handle; self.payload = payload }
}

public enum MobilePushSendOutcome: Sendable, Equatable {
    case sent
    /// The relay no longer knows the handle, it is another host's, or FCM dropped the token.
    case gone
    case retry(after: TimeInterval?)
    case failed(status: Int)
    public init(status: Int, retryAfter: TimeInterval?) {
        switch status {
        case 200...299: self = .sent
        case 403, 404, 410: self = .gone
        case 429, 500, 502, 504: self = .retry(after: retryAfter)
        default: self = .failed(status: status)
        }
    }
}

public protocol MobilePushTransport: Sendable {
    func send(_ request: MobilePushSendRequest, to url: URL, hostToken: String) async -> MobilePushSendOutcome
}

public struct MobilePushHTTPTransport: MobilePushTransport {
    /// The longest Retry-After taken from the relay, which clamps its own to the same.
    public static let maximumRetryAfter: TimeInterval = 3600
    private let session: URLSession
    public init(session: URLSession = URLSession(configuration: .ephemeral)) { self.session = session }
    public func send(_ request: MobilePushSendRequest, to url: URL, hostToken: String) async -> MobilePushSendOutcome {
        var http = URLRequest(url: url)
        http.httpMethod = "POST"
        http.timeoutInterval = 15
        http.setValue("application/json", forHTTPHeaderField: "Content-Type")
        http.setValue("Bearer " + hostToken, forHTTPHeaderField: "Authorization")
        http.httpBody = try? JSONEncoder().encode(request)
        guard let (_, response) = try? await session.data(for: http, delegate: MobilePushNoRedirect.shared), let answer = response as? HTTPURLResponse else { return .retry(after: nil) }
        return MobilePushSendOutcome(status: answer.statusCode, retryAfter: Self.retryAfter(answer.value(forHTTPHeaderField: "Retry-After")))
    }
    /// Seconds only (an HTTP date counts as absent), finite, not negative, at most `maximumRetryAfter`.
    static func retryAfter(_ header: String?) -> TimeInterval? {
        guard let header, let seconds = TimeInterval(header.trimmingCharacters(in: .whitespaces)), seconds.isFinite, seconds >= 0 else { return nil }
        return min(seconds, maximumRetryAfter)
    }
}

/// A relay that answers 3xx is not followed: the bearer token and the envelope go to the relay
/// the user set and nowhere else, and the 3xx itself comes back as `.failed(status:)`.
final class MobilePushNoRedirect: NSObject, URLSessionTaskDelegate, Sendable {
    static let shared = MobilePushNoRedirect()
    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) { completionHandler(nil) }
}
