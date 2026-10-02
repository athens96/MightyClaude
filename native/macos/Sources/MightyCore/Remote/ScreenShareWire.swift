import Foundation

// MARK: - Network, codec and quality

/// How the phone reached the Mac for this session. The caps below differ by
/// network because mobile data is paid for by the megabyte.
public enum ScreenShareNetworkKind: String, Codable, Sendable, Equatable {
    case wifi, cellular
}

/// Which ICE candidate pair carried the video. `host` is the same Wi-Fi, the
/// reflexive kinds are P2P across NAT, and `relay` is the coturn fallback —
/// the only path with a bandwidth quota the sender has to respect.
public enum ScreenShareIcePath: String, Codable, Sendable, Equatable {
    case host, srflx, prflx, relay
}

/// The codecs this host will negotiate. HEVC is deliberately absent: its
/// screen-content behaviour is worse than H.264's at the same bitrate and the
/// licence story is worse again.
public enum ScreenShareVideoCodec: String, Codable, Sendable, Equatable {
    case h264 = "H264"
    case vp9 = "VP9"
    case av1 = "AV1"
}

/// Resolution, frame rate and bitrate ceiling for one session.
public struct ScreenShareQualityProfile: Codable, Sendable, Equatable {
    public var width: Int
    public var height: Int
    public var fps: Int
    public var maxBitrateKbps: Int

    public init(width: Int, height: Int, fps: Int, maxBitrateKbps: Int) {
        self.width = width; self.height = height; self.fps = fps; self.maxBitrateKbps = maxBitrateKbps
    }
}

/// Picks the quality ceiling for a session: Wi-Fi up to 1080p30 at ~6 Mbps,
/// mobile data up to 720p15 at ~1 Mbps, and a TURN-relayed path clamped again
/// to the per-session coturn quota so one phone cannot eat the whole server.
public enum ScreenShareQuality {
    public static let wifiCeiling = ScreenShareQualityProfile(width: 1920, height: 1080, fps: 30, maxBitrateKbps: 6000)
    public static let cellularCeiling = ScreenShareQualityProfile(width: 1280, height: 720, fps: 15, maxBitrateKbps: 1000)
    /// coturn's per-session allowance (`max-bps=250000` bytes/s = 2 Mbps).
    public static let turnSessionQuotaKbps = 2000
    /// The slowest the stream ever runs; below this a terminal feels broken.
    public static let minimumFps = 5

    /// - Parameters:
    ///   - network: what the phone reported at session start.
    ///   - path: the selected candidate pair, once ICE has settled. A relayed
    ///     path lowers the bitrate to the TURN quota.
    ///   - displayWidth/displayHeight: the captured display in points, so the
    ///     stream keeps the display's aspect ratio instead of being letterboxed
    ///     into the ceiling's.
    public static func profile(
        network: ScreenShareNetworkKind,
        path: ScreenShareIcePath = .host,
        displayWidth: Int,
        displayHeight: Int
    ) -> ScreenShareQualityProfile {
        let ceiling = network == .wifi ? wifiCeiling : cellularCeiling
        var profile = ceiling
        let size = fit(width: displayWidth, height: displayHeight,
                       maxWidth: ceiling.width, maxHeight: ceiling.height)
        profile.width = size.width
        profile.height = size.height
        if path == .relay { profile.maxBitrateKbps = min(profile.maxBitrateKbps, turnSessionQuotaKbps) }
        profile.fps = max(minimumFps, profile.fps)
        return profile
    }

    /// Scales `width`×`height` down to fit the ceiling, keeping the aspect ratio
    /// and even dimensions (every hardware encoder wants those).
    static func fit(width: Int, height: Int, maxWidth: Int, maxHeight: Int) -> (width: Int, height: Int) {
        guard width > 0, height > 0 else { return (maxWidth, maxHeight) }
        let scale = min(1, min(Double(maxWidth) / Double(width), Double(maxHeight) / Double(height)))
        let scaled = (Int((Double(width) * scale).rounded()), Int((Double(height) * scale).rounded()))
        return (max(2, scaled.0 - scaled.0 % 2), max(2, scaled.1 - scaled.1 % 2))
    }
}

/// What the codec choice is allowed to look at.
public struct ScreenShareCodecConditions: Sendable, Equatable {
    public var network: ScreenShareNetworkKind
    /// What the phone said it can decode (`RTCRtpReceiver.getCapabilities`).
    public var phoneDecodes: [ScreenShareVideoCodec]
    /// Share of CPU still free on the Mac, 0…1.
    public var cpuHeadroom: Double
    /// True once the Mac is thermally throttled.
    public var thermalPressure: Bool

    public init(network: ScreenShareNetworkKind, phoneDecodes: [ScreenShareVideoCodec],
                cpuHeadroom: Double, thermalPressure: Bool) {
        self.network = network; self.phoneDecodes = phoneDecodes
        self.cpuHeadroom = cpuHeadroom; self.thermalPressure = thermalPressure
    }
}

/// Hardware VideoToolbox H.264 is the default everywhere. A software VP9 or
/// AV1 encoder only earns its CPU on mobile data, where the bytes it saves are
/// the point — and only while the Mac has headroom and is not throttled.
public enum ScreenShareCodecPolicy {
    /// Below this much free CPU, a software encoder would starve the rest of
    /// the app, so H.264 stays.
    public static let minimumCPUHeadroom = 0.35
    /// Tried in this order; AV1 saves the most bytes per pixel.
    public static let cellularPreference: [ScreenShareVideoCodec] = [.av1, .vp9]

    public static func choose(_ conditions: ScreenShareCodecConditions) -> ScreenShareVideoCodec {
        guard conditions.network == .cellular,
              !conditions.thermalPressure,
              conditions.cpuHeadroom >= minimumCPUHeadroom
        else { return .h264 }
        for codec in cellularPreference where conditions.phoneDecodes.contains(codec) { return codec }
        return .h264
    }

    /// Called while a session runs: once the Mac heats up or runs out of CPU,
    /// the stream drops back to hardware H.264 and the peer takes an ICE-free
    /// renegotiation.
    public static func mustFallBack(to current: ScreenShareVideoCodec, conditions: ScreenShareCodecConditions) -> Bool {
        current != .h264 && (conditions.thermalPressure || conditions.cpuHeadroom < minimumCPUHeadroom)
    }
}

// MARK: - TURN credentials

/// One ICE server as the phone's `RTCConfiguration` wants it.
public struct ScreenShareIceServer: Codable, Sendable, Equatable {
    public var urls: [String]
    public var username: String?
    public var credential: String?

    public init(urls: [String], username: String? = nil, credential: String? = nil) {
        self.urls = urls; self.username = username; self.credential = credential
    }

    public var json: [String: Any] {
        var object: [String: Any] = ["urls": urls]
        if let username { object["username"] = username }
        if let credential { object["credential"] = credential }
        return object
    }
}

/// A short-lived HMAC credential the relay minted for this host. The coturn
/// shared secret stays on the relay: this is all the Mac ever sees, and all it
/// forwards to the phone inside E2EE.
public struct ScreenShareTurnCredential: Sendable, Equatable {
    public var username: String
    public var password: String
    public var uris: [String]
    public var ttl: TimeInterval
    public var issuedAt: Date

    /// Renewed this long before expiry, so the ICE restart finishes while the
    /// old credential is still valid and the stream never drops.
    public static let renewalMargin: TimeInterval = 300

    public init(username: String, password: String, uris: [String], ttl: TimeInterval, issuedAt: Date) {
        self.username = username; self.password = password
        self.uris = uris; self.ttl = ttl; self.issuedAt = issuedAt
    }

    public var expiresAt: Date { issuedAt.addingTimeInterval(ttl) }
    public func needsRenewal(now: Date) -> Bool { now >= expiresAt.addingTimeInterval(-Self.renewalMargin) }
    public func isExpired(now: Date) -> Bool { now >= expiresAt }

    /// TURN plus the STUN endpoint on the same server: same Wi-Fi gets there on
    /// host candidates, and everything else needs a reflexive address first.
    public var iceServers: [ScreenShareIceServer] {
        var servers: [ScreenShareIceServer] = []
        let stun = uris.compactMap(Self.stunURI)
        if !stun.isEmpty { servers.append(ScreenShareIceServer(urls: Array(Set(stun)).sorted())) }
        if !uris.isEmpty { servers.append(ScreenShareIceServer(urls: uris, username: username, credential: password)) }
        return servers
    }

    /// `turn:host:3478?transport=udp` → `stun:host:3478`.
    static func stunURI(_ turn: String) -> String? {
        guard turn.hasPrefix("turn:") || turn.hasPrefix("turns:") else { return nil }
        let withoutScheme = turn.drop(while: { $0 != ":" }).dropFirst()
        let authority = withoutScheme.split(separator: "?", maxSplits: 1).first ?? ""
        return authority.isEmpty ? nil : "stun:" + authority
    }

    /// Reads the relay's plaintext `turn-credentials` frame (docs/relay.md).
    public static func parse(relayFrame object: [String: Any], now: Date) -> ScreenShareTurnCredential? {
        guard let username = object["username"] as? String, !username.isEmpty,
              let password = object["password"] as? String, !password.isEmpty,
              let uris = object["uris"] as? [String], !uris.isEmpty,
              uris.allSatisfy({ $0.hasPrefix("turn:") || $0.hasPrefix("turns:") })
        else { return nil }
        let ttl = (object["ttl"] as? NSNumber)?.doubleValue ?? 3600
        guard ttl > 0 else { return nil }
        return ScreenShareTurnCredential(username: username, password: password, uris: uris,
                                         ttl: min(ttl, 86_400), issuedAt: now)
    }
}

// MARK: - Displays

/// One display as the phone's switcher lists it.
public struct ScreenShareDisplayInfo: Codable, Sendable, Equatable {
    public var displayId: UInt32
    public var width: Int
    public var height: Int
    public var main: Bool

    public init(displayId: UInt32, width: Int, height: Int, main: Bool) {
        self.displayId = displayId; self.width = width; self.height = height; self.main = main
    }

    public var json: [String: Any] {
        ["displayId": NSNumber(value: displayId), "width": width, "height": height, "main": main]
    }
}

public extension ScreenShareDisplaySource {
    /// The display list for `/m1/screen-share/state`, main display first.
    func info() -> [ScreenShareDisplayInfo] {
        let main = mainDisplayId()
        return activeDisplayIds().compactMap { id in
            guard let bounds = bounds(of: id) else { return nil }
            return ScreenShareDisplayInfo(displayId: id, width: Int(bounds.width),
                                          height: Int(bounds.height), main: id == main)
        }
    }
}

// MARK: - Signalling

/// The wire reasons of `screen-session-end` and `screen-kill` (docs/relay.md).
public enum ScreenShareWireReason {
    public static func sessionEnd(_ reason: ScreenShareStopReason) -> String {
        switch reason {
        case .idleTimeout: return "idle-timeout"
        case .peerLeft: return "peer-left"
        default: return "peer-left"
        }
    }

    /// `screen-kill` reasons. A stop the phone should read as a kill rather than
    /// a normal end maps here; anything else is nil and goes out as an end.
    public static func kill(_ reason: ScreenShareStopReason) -> String? {
        switch reason {
        case .revoked: return "revoked"
        case .grantDowngrade: return "grant-downgrade"
        case .rekeyPairing: return "rekey-pairing"
        case .killSwitch: return "kill-switch"
        case .lockScreen: return "lock-screen"
        case .secureInput: return "secure-input"
        case .concurrencyLimit: return "concurrency-limit"
        case .idleTimeout, .peerLeft: return nil
        }
    }
}

/// One signalling message of the screen-share contract. These travel as JSON
/// inside the existing E2EE relay channel, so the relay sees ciphertext only.
public enum ScreenShareSignal: Sendable, Equatable {
    case offer(sessionId: String, sdp: String, mode: ScreenShareGrant, displayId: UInt32,
               codec: ScreenShareVideoCodec, quality: ScreenShareQualityProfile, iceRestart: Bool)
    case answer(sessionId: String, sdp: String)
    case ice(sessionId: String, candidate: String, sdpMid: String?, sdpMLineIndex: Int?, usernameFragment: String?)
    case sessionEnd(sessionId: String, reason: String)
    case grant(sessionId: String?, allowed: Bool, grant: ScreenShareGrant,
               controlChallengeB64: String?, iceServers: [ScreenShareIceServer]?,
               displays: [ScreenShareDisplayInfo]?)
    case kill(sessionId: String?, reason: String)

    /// One plaintext signalling frame never exceeds this: the relay drops a
    /// binary frame over 1 MiB and closes a socket at 4 MiB buffered, and a
    /// 64 KiB ceiling keeps a long SDP far from either.
    public static let maximumPlaintextBytes = 64 * 1_024

    public var type: String {
        switch self {
        case .offer: return "screen-offer"
        case .answer: return "screen-answer"
        case .ice: return "screen-ice"
        case .sessionEnd: return "screen-session-end"
        case .grant: return "screen-grant"
        case .kill: return "screen-kill"
        }
    }

    public var sessionId: String? {
        switch self {
        case .offer(let id, _, _, _, _, _, _), .answer(let id, _), .ice(let id, _, _, _, _),
             .sessionEnd(let id, _):
            return id
        case .grant(let id, _, _, _, _, _), .kill(let id, _):
            return id
        }
    }

    /// The JSON object that goes inside the encrypted frame.
    public var json: [String: Any] {
        var object: [String: Any] = ["type": type]
        if let sessionId { object["sessionId"] = sessionId }
        switch self {
        case .offer(_, let sdp, let mode, let displayId, let codec, let quality, let iceRestart):
            object["sdp"] = sdp
            object["mode"] = mode.rawValue
            object["displayId"] = NSNumber(value: displayId)
            object["codec"] = codec.rawValue
            object["quality"] = ["width": quality.width, "height": quality.height,
                                 "fps": quality.fps, "maxBitrateKbps": quality.maxBitrateKbps]
            object["iceRestart"] = iceRestart
        case .answer(_, let sdp):
            object["sdp"] = sdp
        case .ice(_, let candidate, let sdpMid, let sdpMLineIndex, let usernameFragment):
            object["candidate"] = candidate
            if let sdpMid { object["sdpMid"] = sdpMid }
            if let sdpMLineIndex { object["sdpMLineIndex"] = sdpMLineIndex }
            if let usernameFragment { object["usernameFragment"] = usernameFragment }
        case .sessionEnd(_, let reason), .kill(_, let reason):
            object["reason"] = reason
        case .grant(_, let allowed, let grant, let challenge, let iceServers, let displays):
            object["allowed"] = allowed
            object["grant"] = grant.rawValue
            if let challenge { object["controlChallengeB64"] = challenge }
            if let iceServers { object["iceServers"] = iceServers.map(\.json) }
            if let displays { object["displays"] = displays.map(\.json) }
        }
        return object
    }

    /// Serializes the frame, refusing one that would not fit the contract.
    public func encoded() -> Data? {
        guard let data = try? JSONSerialization.data(withJSONObject: json, options: [.sortedKeys]),
              data.count <= Self.maximumPlaintextBytes
        else { return nil }
        return data
    }

    /// Reads what a phone may send: an answer, one ICE candidate, or an end.
    /// Anything else — including a host-only type a hostile phone echoes back —
    /// is nil, so a phone can never pose as the Mac's own signalling.
    public static func inbound(_ object: [String: Any]) -> ScreenShareSignal? {
        guard let type = object["type"] as? String else { return nil }
        guard let sessionId = object["sessionId"] as? String,
              !sessionId.isEmpty, sessionId.utf8.count <= 64 else { return nil }
        switch type {
        case "screen-answer":
            guard let sdp = object["sdp"] as? String, !sdp.isEmpty,
                  sdp.utf8.count <= maximumPlaintextBytes else { return nil }
            return .answer(sessionId: sessionId, sdp: sdp)
        case "screen-ice":
            // An empty candidate is the documented end-of-candidates marker.
            guard let candidate = object["candidate"] as? String,
                  candidate.utf8.count <= 4_096 else { return nil }
            let index = (object["sdpMLineIndex"] as? NSNumber)?.intValue
            return .ice(sessionId: sessionId, candidate: candidate,
                        sdpMid: object["sdpMid"] as? String, sdpMLineIndex: index,
                        usernameFragment: object["usernameFragment"] as? String)
        case "screen-session-end":
            let reason = object["reason"] as? String ?? "user-stop"
            guard ["user-stop", "background", "peer-failed"].contains(reason) else { return nil }
            return .sessionEnd(sessionId: sessionId, reason: reason)
        default:
            return nil
        }
    }
}
