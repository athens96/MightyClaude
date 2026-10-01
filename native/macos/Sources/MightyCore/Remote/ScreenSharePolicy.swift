import CoreGraphics
import CryptoKit
import Foundation

// MARK: - Data types

/// Session grant level for a paired phone.
public enum ScreenShareGrant: String, Codable, Sendable, Equatable, Comparable {
    case none = "none"
    case view = "view"
    case control = "control"

    private static let order: [ScreenShareGrant] = [.none, .view, .control]
    public static func < (lhs: Self, rhs: Self) -> Bool {
        (Self.order.firstIndex(of: lhs) ?? 0) < (Self.order.firstIndex(of: rhs) ?? 0)
    }
}

/// Per-phone allow-list entry stored by the host.
/// Newly paired phones start with `allowed = false` and `grant = .none`.
public struct ScreenShareDeviceSettings: Codable, Sendable, Equatable {
    public var deviceId: String
    /// Allow-list flag. False for every newly paired phone until the user
    /// explicitly enables it in Mac settings.
    public var allowed: Bool
    /// Separate grant for view vs. control.
    public var grant: ScreenShareGrant
    /// ANSI X9.62 uncompressed P-256 public key (65 bytes) enrolled by the
    /// phone when control is granted. Nil until control has been granted once.
    public var controlKeyPublicData: Data?

    public init(deviceId: String, allowed: Bool = false, grant: ScreenShareGrant = .none,
                controlKeyPublicData: Data? = nil) {
        self.deviceId = deviceId; self.allowed = allowed
        self.grant = grant; self.controlKeyPublicData = controlKeyPublicData
    }
}

/// One entry in the session log. Keystrokes are never included here.
public struct ScreenSessionEntry: Sendable, Equatable {
    public var sessionId: String
    public var deviceId: String
    public var mode: ScreenShareGrant
    public var startedAt: Date
    public var endedAt: Date?

    public init(sessionId: String, deviceId: String, mode: ScreenShareGrant,
                startedAt: Date, endedAt: Date? = nil) {
        self.sessionId = sessionId; self.deviceId = deviceId; self.mode = mode
        self.startedAt = startedAt; self.endedAt = endedAt
    }
}

/// Why a session or the capture subsystem was stopped.
public enum ScreenShareStopReason: Sendable, Equatable {
    case revoked, grantDowngrade, rekeyPairing, killSwitch
    case idleTimeout, peerLeft, lockScreen, secureInput, concurrencyLimit
}

/// t0 (trigger) and t1 (all stopped) on the Mac clock for the ≤1 s guarantee.
public struct ScreenShareKillTiming: Sendable, Equatable {
    public var t0: Date
    public var t1: Date
    public var reason: ScreenShareStopReason
    /// True when a surface had not finished by the deadline and was cut off.
    /// The host's own state was already safe at t0; this records that the
    /// PeerConnection or the capture stream was abandoned rather than awaited.
    public var deadlineExceeded: Bool
    public var elapsed: TimeInterval { t1.timeIntervalSince(t0) }

    public init(t0: Date, t1: Date, reason: ScreenShareStopReason, deadlineExceeded: Bool = false) {
        self.t0 = t0; self.t1 = t1; self.reason = reason; self.deadlineExceeded = deadlineExceeded
    }
}

/// The capture, peer and teardown controls of one session. Injected, so the
/// host's rules run without a display, a relay or a real PeerConnection.
public struct ScreenShareSurface: Sendable {
    /// Begins frame capture. Called when the first session starts and again
    /// when the lock screen or secure input clears.
    public var startCapture: @Sendable () async -> Void
    /// Stops frame capture.
    public var stopCapture: @Sendable () async -> Void
    /// Closes the PeerConnection for this session.
    public var closePeer: @Sendable () async -> Void

    public init(
        startCapture: @escaping @Sendable () async -> Void,
        stopCapture: @escaping @Sendable () async -> Void,
        closePeer: @escaping @Sendable () async -> Void
    ) {
        self.startCapture = startCapture; self.stopCapture = stopCapture; self.closePeer = closePeer
    }
}

/// One live session, as the menu-bar indicator and the session list see it.
public struct ScreenShareLiveSession: Sendable, Equatable {
    public var sessionId: String
    public var deviceId: String
    public var mode: ScreenShareGrant
    public var startedAt: Date

    public init(sessionId: String, deviceId: String, mode: ScreenShareGrant, startedAt: Date) {
        self.sessionId = sessionId; self.deviceId = deviceId; self.mode = mode; self.startedAt = startedAt
    }
}

/// Normalized 0–1 display coordinate plus the display identifier.
public struct ScreenShareNormalizedPoint: Sendable, Equatable {
    public var displayId: UInt32
    /// Horizontal position in 0…1, where 0 is the left edge of the display.
    public var x: Double
    /// Vertical position in 0…1, where 0 is the top edge of the display.
    public var y: Double

    public init(displayId: UInt32, x: Double, y: Double) {
        self.displayId = displayId; self.x = x; self.y = y
    }
}

/// Reasons a join attempt fails.
public enum ScreenShareError: Error, Sendable, Equatable {
    case deviceNotAllowed
    case insufficientGrant
    case controlSignatureInvalid
    case concurrencyLimit
}

// MARK: - Pure policy rules

/// Pure, stateless safety rules for screen-share.
/// No I/O, so the complete logic is unit-testable without a display or relay.
public enum ScreenSharePolicy {
    public static let controlIdleTimeout: TimeInterval = 600   // 10 min
    public static let viewIdleTimeout: TimeInterval = 1800     // 30 min
    public static let maxControllers = 1
    public static let maxViewers = 2
    /// Local HID activity pauses remote injection for this many seconds.
    public static let hidPauseDuration: TimeInterval = 2
    /// All capture/injection/PeerConnection must stop within this interval.
    public static let killDeadline: TimeInterval = 1
    /// Phone backgrounding ends the session after this many seconds.
    public static let backgroundTimeout: TimeInterval = 30

    // MARK: Allow-list

    /// A newly paired phone has no settings entry; that also means denied.
    public static func isAllowed(_ settings: ScreenShareDeviceSettings?) -> Bool {
        settings?.allowed == true
    }

    // MARK: Grant

    /// View grant covers view; control grant covers both. No grant covers neither.
    public static func grantAllows(grant: ScreenShareGrant, requestedMode: ScreenShareGrant) -> Bool {
        switch requestedMode {
        case .none:    return true
        case .view:    return grant >= .view
        case .control: return grant == .control
        }
    }

    // MARK: Concurrency

    public struct SessionCounts: Sendable, Equatable {
        public var controllers: Int
        public var viewers: Int
        public init(controllers: Int = 0, viewers: Int = 0) {
            self.controllers = controllers; self.viewers = viewers
        }
    }

    public static func canJoin(mode: ScreenShareGrant, counts: SessionCounts) -> Bool {
        switch mode {
        case .none:    return true
        case .control: return counts.controllers < maxControllers
        case .view:    return counts.viewers < maxViewers
        }
    }

    // MARK: Coordinate mapping

    /// Maps a normalized 0–1 point to a pixel position within the display bounds.
    public static func map(point: ScreenShareNormalizedPoint, bounds: CGRect) -> CGPoint {
        CGPoint(
            x: bounds.origin.x + max(0, min(1, point.x)) * bounds.width,
            y: bounds.origin.y + max(0, min(1, point.y)) * bounds.height
        )
    }

    // MARK: Idle timeout

    public static func idleTimeout(for mode: ScreenShareGrant) -> TimeInterval {
        mode == .control ? controlIdleTimeout : viewIdleTimeout
    }

    // MARK: Injection guard

    /// Returns true when the host must reject injection regardless of grant level.
    public static func injectionBlocked(locked: Bool, secureInputActive: Bool) -> Bool {
        locked || secureInputActive
    }

    // MARK: Control-key signature verification

    /// The deterministic challenge bytes the Mac sends before each control session.
    public static func controlChallenge(sessionId: String, timestamp: String) -> Data {
        Data("screen-control-challenge:\(sessionId):\(timestamp)".utf8)
    }

    /// Verifies an ECDSA-P256 DER signature over SHA-256(challenge).
    /// The Android Keystore uses P-256; the Mac stores the public key on grant.
    public static func verifyControlSignature(challenge: Data, signature: Data, publicKeyData: Data) -> Bool {
        guard let key = try? P256.Signing.PublicKey(x963Representation: publicKeyData),
              let sig = try? P256.Signing.ECDSASignature(derRepresentation: signature)
        else { return false }
        return key.isValidSignature(sig, for: SHA256.hash(data: challenge))
    }
}
