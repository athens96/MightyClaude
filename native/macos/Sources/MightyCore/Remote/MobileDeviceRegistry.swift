import CryptoKit
import Foundation

/// One phone the host has admitted. The token itself is never stored: only its
/// SHA-256, so a stolen `devices.json` cannot be replayed as a phone.
public struct MobileDevice: Codable, Sendable, Equatable, Identifiable {
    public var id: String
    public var name: String
    /// Hex SHA-256 of the issued token; nil for the "구버전 앱" group, which has
    /// no token and authenticates with the pairing key every time.
    public var tokenHash: String?
    public var firstSeen: String
    public var lastSeen: String
    public init(id: String, name: String, tokenHash: String? = nil, firstSeen: String, lastSeen: String) {
        self.id = id; self.name = name; self.tokenHash = tokenHash; self.firstSeen = firstSeen; self.lastSeen = lastSeen
    }
    public var legacy: Bool { tokenHash == nil }
}

/// What the Mac's settings list shows for one device. The hash never leaves
/// the registry, so this shape carries no secret at all.
public struct MobileDeviceInfo: Codable, Sendable, Equatable, Identifiable {
    public var id: String
    public var name: String
    public var firstSeen: String
    public var lastSeen: String
    public var connected: Bool
    public var legacy: Bool
    /// Registered within the last day. A phone the user did not add themselves
    /// has to be visible as an arrival, not hidden among the older rows.
    public var isNew: Bool
    public init(id: String, name: String, firstSeen: String, lastSeen: String, connected: Bool, legacy: Bool, isNew: Bool = false) {
        self.id = id; self.name = name; self.firstSeen = firstSeen; self.lastSeen = lastSeen; self.connected = connected; self.legacy = legacy; self.isNew = isNew
    }
}

/// What an auth frame leads to (docs/relay.md, "기기 토큰"). Kept apart from the
/// socket so every branch — including the frames older apps still send — can be
/// exercised without a relay.
public enum MobileAuthDecision: Sendable, Equatable {
    /// A returning phone that proved its token; no new token is issued.
    case token(deviceId: String)
    /// First pairing with a valid key: the token travels in this `auth_ok` only.
    /// Nil when the list could not be written — the phone stays on its pairing
    /// key and is handed a token the next time it connects.
    case paired(deviceId: String, token: String?)
    /// An app that knows nothing about tokens, admitted on the key alone.
    case legacy
    case refused(reason: String)
}

/// What a first pairing did. The words are the auth vocabulary of
/// docs/relay.md: a taken id is "device-conflict", a full list or too many
/// registrations in an hour is "device-limit".
public enum MobileTokenIssue: Sendable, Equatable {
    case issued(String)
    /// The id already holds a token, so it belongs to a phone that is not this
    /// one. Nothing about the stored row changes.
    case conflict
    /// No slot and nothing stale enough to evict, or too many registrations
    /// this hour.
    case refused
    /// Minted but not persisted: the caller admits the connection without a
    /// token rather than handing out one the host will not recognise later.
    case unsaved
}

public enum MobileAuthSupport {
    /// Reads one decrypted `auth` frame. The pairing key is compared by hash in
    /// constant time; neither the key nor the token is returned to the caller
    /// except as the one token a first pairing is entitled to.
    public static func decide(frame: [String: Any], pairingKey: String, devices: MobileDeviceRegistry, allowLegacy: Bool = true) -> MobileAuthDecision {
        guard frame["type"] as? String == "auth" else { return .refused(reason: "malformed") }
        let name = MobileDeviceRegistry.deviceName(frame["clientName"] as? String)
        // A `clientId` that is not the identifier the app mints is a malformed
        // frame. Downgrading it to the key-only path would let any phone join
        // the shared "구버전 앱" row simply by sending rubbish in that field.
        var clientId: String?
        if let raw = frame["clientId"] as? String {
            guard MobileDeviceRegistry.validClientId(raw) else { return .refused(reason: "malformed") }
            clientId = raw
        }
        let presented = frame["pairingKey"] as? String
        // A returning phone shows its token and no key at all.
        if presented == nil, let token = frame["deviceToken"] as? String, token.utf8.count <= 256 {
            guard let clientId, devices.authenticate(clientId: clientId, token: token) else { return .refused(reason: "device-revoked") }
            return .token(deviceId: clientId)
        }
        guard let presented, presented.utf8.count <= 256 else { return .refused(reason: "malformed") }
        guard MobileDeviceRegistry.constantTimeEquals(MobileDeviceRegistry.hash(presented), MobileDeviceRegistry.hash(pairingKey)) else {
            return .refused(reason: "pairing-key")
        }
        // An app that knows about tokens gets one, exactly once, right here.
        if let clientId {
            switch devices.issueToken(clientId: clientId, name: name) {
            case .issued(let token): return .paired(deviceId: clientId, token: token)
            case .unsaved: return .paired(deviceId: clientId, token: nil)
            case .conflict: return .refused(reason: "device-conflict")
            case .refused: return .refused(reason: "device-limit")
            }
        }
        // An older app: admitted on the key, shown as one "구버전 앱" row because
        // there is nothing to tell such phones apart by — unless the user has
        // asked this host to take token-carrying phones only.
        guard allowLegacy else { return .refused(reason: "legacy-refused") }
        devices.touchLegacy()
        return .legacy
    }

    /// Which live connections owe their place to the pairing key: the ones
    /// still inside the handshake, the "구버전 앱" group, and any device the
    /// registry holds no token for. A phone with a token never presents the
    /// key, so rotating it must leave that phone connected.
    public static func keyDependent(connections: [String], devices: [String: String], tokenHolders: Set<String>) -> [String] {
        connections.filter { connection in
            guard let device = devices[connection] else { return true }
            if device == MobileDeviceRegistry.legacyId { return true }
            return !tokenHolders.contains(device)
        }
    }
}

/// The host's device list (docs/relay.md, "기기 토큰"). A phone pairs once with
/// the pairing key and is handed a token it keeps; later connections present
/// the token instead, so revoking one phone leaves the others working.
///
/// A lock rather than an actor: the settings sheet reads the list from the
/// service's synchronous `status()`, and the file is a few hundred bytes.
public final class MobileDeviceRegistry: @unchecked Sendable {
    public static let maximum = 32
    /// The id every phone that predates device tokens is grouped under.
    public static let legacyId = "legacy"
    public static let legacyName = "구버전 앱"
    /// A connected phone touches `lastSeen` at most this often; without the
    /// throttle every long poll would rewrite the file.
    public static let lastSeenInterval: TimeInterval = 60
    public static let tokenBytes = 32
    public static let maximumNameLength = 40
    /// How long a phone must have been unseen before a full list will drop it
    /// for a newcomer. Anything shorter turns the pairing key into a way of
    /// evicting the user's own phones one registration at a time.
    public static let staleAfter: TimeInterval = 90 * 24 * 3_600
    /// A host-wide ceiling on first pairings, so a leaked key cannot churn the
    /// list — or the file — as fast as a script can connect.
    public static let registrationWindow: TimeInterval = 3_600
    public static let maximumRegistrations = 8
    /// How long a row is shown as an arrival in the settings list.
    public static let newDeviceWindow: TimeInterval = 24 * 3_600

    private let url: URL
    private let now: @Sendable () -> Date
    private let lock = NSLock()
    private var loaded = false
    private var devices: [MobileDevice] = []
    /// When the file on disk could not be read: its bytes are copied aside
    /// before the first overwrite, and the sheet says so.
    private var corruptPending = false
    private var warningText: String?
    /// First pairings inside the rolling window, for the rate limit.
    private var registrations: [Date] = []

    public init(url: URL, now: @escaping @Sendable () -> Date = Date.init) {
        self.url = url; self.now = now
    }

    // MARK: Reading

    public func all() -> [MobileDevice] {
        lock.lock(); defer { lock.unlock() }
        load()
        return devices
    }

    public func contains(_ id: String) -> Bool {
        lock.lock(); defer { lock.unlock() }
        load()
        return devices.contains { $0.id == id }
    }

    /// The rows the settings sheet draws, with the arrival badge decided here
    /// where the registry's own clock lives.
    public func infos(connected: Set<String>) -> [MobileDeviceInfo] {
        lock.lock(); defer { lock.unlock() }
        load()
        let stamp = now()
        return devices.map { device in
            MobileDeviceInfo(id: device.id, name: device.name, firstSeen: device.firstSeen, lastSeen: device.lastSeen,
                             connected: connected.contains(device.id), legacy: device.legacy,
                             isNew: Self.isNew(firstSeen: device.firstSeen, now: stamp))
        }
    }

    /// The devices whose right to connect is a token rather than the key.
    public func tokenHolders() -> Set<String> {
        lock.lock(); defer { lock.unlock() }
        load()
        return Set(devices.filter { $0.tokenHash != nil }.map(\.id))
    }

    /// What the settings sheet must tell the user about the file itself, if
    /// anything. Nil in the ordinary case.
    public func warning() -> String? {
        lock.lock(); defer { lock.unlock() }
        load()
        return warningText
    }

    public static func isNew(firstSeen: String, now: Date) -> Bool {
        guard let date = AgentRunTiming.parseTimestamp(firstSeen) else { return false }
        let age = now.timeIntervalSince(date)
        return age >= 0 && age < newDeviceWindow
    }

    /// Whether `clientId` looks like the b64url identifier the app mints once.
    public static func validClientId(_ value: String) -> Bool {
        value.range(of: "^[A-Za-z0-9_-]{16,64}$", options: .regularExpression) != nil
    }
    public static func validToken(_ value: String) -> Bool {
        value.range(of: "^[A-Za-z0-9_-]{32,128}$", options: .regularExpression) != nil
    }

    /// A phone's own name, bounded and stripped of control and invisible
    /// characters. Empty or missing names become "휴대폰" rather than an
    /// invisible row.
    public static func deviceName(_ raw: String?) -> String {
        let plain = MobileRemoteSupport.stripInvisibles(raw ?? "")
        let clean = ActivitySupport.clean(plain, maximumBytes: 120, singleLine: true)
        guard !clean.isEmpty else { return "휴대폰" }
        return String(clean.prefix(maximumNameLength))
    }

    // MARK: Authenticating

    /// Admits a returning phone. The stored hash is compared in constant time
    /// so a wrong token cannot be narrowed down by timing the answer.
    public func authenticate(clientId: String, token: String) -> Bool {
        guard Self.validClientId(clientId), Self.validToken(token) else { return false }
        lock.lock(); defer { lock.unlock() }
        load()
        guard let index = devices.firstIndex(where: { $0.id == clientId }), let stored = devices[index].tokenHash else { return false }
        guard Self.constantTimeEquals(stored, Self.hash(token)) else { return false }
        touch(index)
        return true
    }

    /// First pairing: registers the phone and returns the token once. The
    /// caller has already checked the pairing key.
    ///
    /// A row that already holds a token is never written over. Whoever knows
    /// the key would otherwise only have to name another phone's `clientId` to
    /// take its place in the list — and its name, and its history.
    public func issueToken(clientId: String, name: String) -> MobileTokenIssue {
        guard Self.validClientId(clientId) else { return .refused }
        lock.lock(); defer { lock.unlock() }
        load()
        if let existing = devices.first(where: { $0.id == clientId }), existing.tokenHash != nil { return .conflict }
        let stamp = now()
        registrations.removeAll { stamp.timeIntervalSince($0) >= Self.registrationWindow }
        guard registrations.count < Self.maximumRegistrations else { return .refused }
        let snapshot = devices
        devices.removeAll { $0.id == clientId }
        guard makeRoom() else { devices = snapshot; return .refused }
        guard let token = Self.generateToken() else { devices = snapshot; return .unsaved }
        let mark = timestamp()
        devices.append(MobileDevice(id: clientId, name: name, tokenHash: Self.hash(token), firstSeen: mark, lastSeen: mark))
        // A token the host cannot remember is worse than no token: the phone
        // keeps its pairing key and is handed one the next time it connects.
        do { try save() } catch { devices = snapshot; return .unsaved }
        registrations.append(stamp)
        return .issued(token)
    }

    /// A phone that knows no token. Such phones cannot be told apart, so they
    /// share one row under the group's own fixed label rather than letting
    /// whichever connected last rename the lot.
    public func touchLegacy() {
        lock.lock(); defer { lock.unlock() }
        load()
        if let index = devices.firstIndex(where: { $0.id == Self.legacyId }) { touch(index); return }
        let snapshot = devices
        guard makeRoom() else { return }
        let stamp = timestamp()
        devices.append(MobileDevice(id: Self.legacyId, name: Self.legacyName, tokenHash: nil, firstSeen: stamp, lastSeen: stamp))
        // The phone is admitted on its key either way; a list that cannot be
        // written only costs the row, which the next write restores.
        do { try save() } catch { devices = snapshot }
    }

    /// Frees a slot when the list is full. Only a phone nobody has used for
    /// ninety days goes: evicting the coldest row on demand would turn a
    /// leaked pairing key into a way of pushing the user's own phones out.
    /// Caller holds the lock.
    private func makeRoom() -> Bool {
        let deadline = now().addingTimeInterval(-Self.staleAfter)
        while devices.count >= Self.maximum {
            let stale = devices.enumerated().filter { (AgentRunTiming.parseTimestamp($0.element.lastSeen) ?? .distantPast) < deadline }
            guard let coldest = stale.min(by: { $0.element.lastSeen < $1.element.lastSeen })?.offset else { return false }
            devices.remove(at: coldest)
        }
        return true
    }

    /// Bumps `lastSeen` at most once a minute. Caller holds the lock.
    private func touch(_ index: Int) {
        let stamp = now()
        let previous = AgentRunTiming.parseTimestamp(devices[index].lastSeen) ?? .distantPast
        guard stamp.timeIntervalSince(previous) >= Self.lastSeenInterval else { return }
        let was = devices[index].lastSeen
        devices[index].lastSeen = timestamp()
        // A missed write costs a stale timestamp, nothing more: the phone has
        // already proved its token and stays admitted.
        do { try save() } catch { devices[index].lastSeen = was }
    }

    // MARK: Revoking

    /// Removes one row. Throws when the list could not be written, with the
    /// row put back, so the caller reports a failed revoke rather than showing
    /// a device as gone while the file still admits it.
    @discardableResult public func remove(_ id: String) throws -> Bool {
        lock.lock(); defer { lock.unlock() }
        load()
        let snapshot = devices
        devices.removeAll { $0.id == id }
        guard devices.count != snapshot.count else { return false }
        do { try save() } catch { devices = snapshot; throw error }
        return true
    }

    // MARK: Storage

    /// Built per call, like the rest of the core: a shared formatter would be
    /// mutable state crossing the lock for no measurable gain.
    private func timestamp() -> String {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return formatter.string(from: now())
    }

    private static func generateToken() -> String? {
        var random = [UInt8](repeating: 0, count: tokenBytes)
        guard SecRandomCopyBytes(kSecRandomDefault, random.count, &random) == errSecSuccess else { return nil }
        return Data(random).base64EncodedString()
            .replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_").replacingOccurrences(of: "=", with: "")
    }

    public static func hash(_ value: String) -> String {
        SHA256.hash(data: Data(value.utf8)).map { String(format: "%02x", $0) }.joined()
    }

    /// Compares two hex digests without an early exit, so the number of equal
    /// leading characters cannot be read off the answer's timing.
    public static func constantTimeEquals(_ left: String, _ right: String) -> Bool {
        let a = Array(left.utf8), b = Array(right.utf8)
        var difference = UInt8(a.count == b.count ? 0 : 1)
        for index in 0..<max(a.count, b.count) {
            let first: UInt8 = index < a.count ? a[index] : 0
            let second: UInt8 = index < b.count ? b[index] : 0
            difference |= first ^ second
        }
        return difference == 0
    }

    private func load() {
        guard !loaded else { return }
        loaded = true
        guard let data = CLIAccountSupport.boundedData(url, maximumBytes: 256 * 1024) else { return }
        guard let decoded = try? JSONDecoder().decode([MobileDevice].self, from: data) else {
            // Unreadable: start empty rather than locking every phone out, but
            // keep the old bytes before the first write and say so on screen.
            corruptPending = true
            warningText = "기기 목록 파일(devices.json)을 읽지 못해 목록을 새로 시작했습니다. 이전 파일은 같은 폴더에 devices.json.corrupt-… 이름으로 보관합니다. 휴대폰은 QR로 다시 페어링해야 합니다."
            return
        }
        var seen = Set<String>()
        devices = decoded.filter { device in
            guard device.id == Self.legacyId || Self.validClientId(device.id) else { return false }
            // A file written by hand may repeat an id; the first row wins so a
            // later duplicate cannot quietly replace a phone's token hash.
            guard seen.insert(device.id).inserted else { return false }
            if let stored = device.tokenHash { return stored.range(of: "^[0-9a-f]{64}$", options: .regularExpression) != nil }
            return device.id == Self.legacyId
        }
        if devices.count > Self.maximum { devices = Array(devices.prefix(Self.maximum)) }
    }

    /// Owner-only file in an owner-only folder; written through a temporary so
    /// a crash mid-write cannot leave a half list behind. Throws so a caller
    /// that promised the user something — a revoke, a token — can take it back.
    private func save() throws {
        let data = try JSONEncoder().encode(devices)
        let directory = url.deletingLastPathComponent()
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        backupCorruptFile()
        let temporary = directory.appendingPathComponent(url.lastPathComponent + "." + UUID().uuidString)
        var placed = false
        defer { if !placed { try? FileManager.default.removeItem(at: temporary) } }
        guard FileManager.default.createFile(atPath: temporary.path, contents: data, attributes: [.posixPermissions: 0o600]) else {
            throw MightyError("기기 목록을 저장하지 못했습니다.")
        }
        if FileManager.default.fileExists(atPath: url.path) { _ = try FileManager.default.replaceItemAt(url, withItemAt: temporary) }
        else { try FileManager.default.moveItem(at: temporary, to: url) }
        placed = true
        try? FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
    }

    /// Keeps the unreadable bytes before they are overwritten: they may be the
    /// only record of which phones this host had admitted. Caller holds the lock.
    private func backupCorruptFile() {
        guard corruptPending else { return }
        corruptPending = false
        guard FileManager.default.fileExists(atPath: url.path) else { return }
        let stamp = Int(now().timeIntervalSince1970)
        let backup = url.deletingLastPathComponent().appendingPathComponent(url.lastPathComponent + ".corrupt-\(stamp)")
        try? FileManager.default.copyItem(at: url, to: backup)
    }
}
