import Foundation

/// Mac-side enforcement actor for screen-share sessions.
///
/// All safety decisions live here. Capture and peer-connection control are
/// injected via closures so the complete safety logic is testable without a
/// display, a relay, or a PeerConnection.
///
/// Covered rules (AC #2):
///   • allow-list off by default; newly paired phones start denied
///   • view and control grants are separate
///   • control requires a valid Keystore-key signature at each session start
///   • revoke / grant-downgrade / pairing-key regen / kill-switch each stop
///     capture, injection and the PeerConnection in ≤ 1 s (Mac clock)
///   • no injection after t0 of any of the above triggers
///   • idle timeouts: 10 min (control), 30 min (view-only)
///   • concurrency: 1 controller + 2 viewers maximum
///   • local HID activity pauses injection for 2 s
///   • lock screen or secure-input active blocks injection
///   • session log never records keystrokes
///   • normalized display coordinates map to CGDisplayBounds
public actor ScreenShareHost {
    // MARK: - Injected dependencies

    /// Starts or restarts frame capture. Called at most once per session.
    public typealias CaptureControl = @Sendable () async -> Void

    // MARK: - Internal session record

    private struct LiveSession: Sendable {
        var deviceId: String
        var mode: ScreenShareGrant
        var startedAt: Date
        var lastActivity: Date
        var peerClose: CaptureControl
    }

    // MARK: - State

    private var deviceSettings: [String: ScreenShareDeviceSettings] = [:]
    private var sessions: [String: LiveSession] = [:]
    private var sessionLog: [ScreenSessionEntry] = []
    private var captureActive = false
    private var injectionEnabled = false
    private var locked = false
    private var secureInputActive = false
    private var hidPausedUntil: Date?
    private var killTimings: [ScreenShareKillTiming] = []
    private let now: @Sendable () -> Date

    // MARK: - Init

    public init(now: @escaping @Sendable () -> Date = { Date() }) {
        self.now = now
    }

    // MARK: - Allow-list & grants

    public func setDeviceSettings(_ settings: ScreenShareDeviceSettings) {
        deviceSettings[settings.deviceId] = settings
    }

    public func settings(for deviceId: String) -> ScreenShareDeviceSettings? {
        deviceSettings[deviceId]
    }

    // MARK: - Session lifecycle

    /// Evaluates a join request and, if admitted, starts capture and records the session.
    ///
    /// - Parameters:
    ///   - sessionId: Caller-chosen opaque identifier for this session.
    ///   - deviceId:  The paired phone's clientId.
    ///   - requestedMode: `.view` or `.control`.
    ///   - controlChallenge: The challenge bytes the Mac generated.
    ///   - controlSignature: ECDSA-P256 DER signature from the phone (nil for view).
    ///   - startCapture: Invoked once to begin capture (only if capture is not already active).
    ///   - peerClose: Invoked to close the PeerConnection when the session ends.
    public func requestSession(
        sessionId: String,
        deviceId: String,
        requestedMode: ScreenShareGrant,
        controlChallenge: Data?,
        controlSignature: Data?,
        startCapture: CaptureControl,
        peerClose: @escaping CaptureControl
    ) async -> Result<Void, ScreenShareError> {
        let settings = deviceSettings[deviceId]

        guard ScreenSharePolicy.isAllowed(settings) else { return .failure(.deviceNotAllowed) }

        let grant = settings?.grant ?? .none
        guard ScreenSharePolicy.grantAllows(grant: grant, requestedMode: requestedMode) else {
            return .failure(.insufficientGrant)
        }

        if requestedMode == .control {
            guard let challenge = controlChallenge,
                  let sig = controlSignature,
                  let keyData = settings?.controlKeyPublicData,
                  ScreenSharePolicy.verifyControlSignature(
                      challenge: challenge, signature: sig, publicKeyData: keyData)
            else { return .failure(.controlSignatureInvalid) }
        }

        let counts = currentCounts()
        guard ScreenSharePolicy.canJoin(mode: requestedMode, counts: counts) else {
            return .failure(.concurrencyLimit)
        }

        let t = now()
        sessions[sessionId] = LiveSession(
            deviceId: deviceId, mode: requestedMode,
            startedAt: t, lastActivity: t,
            peerClose: peerClose
        )
        sessionLog.append(ScreenSessionEntry(
            sessionId: sessionId, deviceId: deviceId, mode: requestedMode, startedAt: t))

        if !captureActive {
            captureActive = true
            await startCapture()
        }
        if requestedMode == .control { injectionEnabled = true }

        return .success(())
    }

    // MARK: - Kill triggers (≤ 1 s guarantee)

    /// Kills every active session. Logs t0 at entry and t1 when all closed.
    public func killAll(reason: ScreenShareStopReason, stopCapture: CaptureControl) async {
        let t0 = now()
        // Disable injection first so no frame can slip through during teardown.
        injectionEnabled = false
        captureActive = false
        await stopCapture()
        for (sid, session) in sessions {
            await session.peerClose()
            endLog(sessionId: sid)
        }
        sessions = [:]
        let t1 = now()
        killTimings.append(ScreenShareKillTiming(t0: t0, t1: t1))
    }

    /// Kills only the sessions belonging to `deviceId` (revoke / downgrade / rekey).
    public func killDevice(
        deviceId: String, reason: ScreenShareStopReason, stopCapture: CaptureControl
    ) async {
        let t0 = now()
        // Disable injection immediately so the first thing after t0 is safe.
        injectionEnabled = false
        let owned = sessions.filter { $0.value.deviceId == deviceId }
        for (sid, session) in owned {
            await session.peerClose()
            endLog(sessionId: sid)
            sessions.removeValue(forKey: sid)
        }
        if sessions.isEmpty {
            captureActive = false
            await stopCapture()
        }
        let t1 = now()
        killTimings.append(ScreenShareKillTiming(t0: t0, t1: t1))
    }

    // MARK: - Idle timeout (test seam)

    /// Simulates an idle-timeout firing for `sessionId`. In production the real
    /// timer calls this; in tests the harness drives it deterministically.
    public func triggerIdleTimeout(sessionId: String, stopCapture: CaptureControl) async {
        guard let session = sessions[sessionId] else { return }
        let t0 = now()
        injectionEnabled = false
        await session.peerClose()
        endLog(sessionId: sessionId)
        sessions.removeValue(forKey: sessionId)
        if sessions.isEmpty {
            captureActive = false
            await stopCapture()
        }
        let t1 = now()
        killTimings.append(ScreenShareKillTiming(t0: t0, t1: t1))
    }

    // MARK: - Injection gating

    public func canInject() -> Bool {
        guard injectionEnabled else { return false }
        guard !ScreenSharePolicy.injectionBlocked(locked: locked, secureInputActive: secureInputActive) else { return false }
        if let until = hidPausedUntil, now() < until { return false }
        return true
    }

    /// Called when local HID activity is detected; pauses remote injection for
    /// `ScreenSharePolicy.hidPauseDuration` seconds.
    public func notifyLocalHID() {
        hidPausedUntil = now().addingTimeInterval(ScreenSharePolicy.hidPauseDuration)
    }

    public func setLocked(_ v: Bool) { locked = v }
    public func setSecureInput(_ v: Bool) { secureInputActive = v }

    // MARK: - Read-only state

    public var sessionCount: Int { sessions.count }
    public var isCaptureActive: Bool { captureActive }
    public var isInjectionEnabled: Bool { injectionEnabled }
    public func log() -> [ScreenSessionEntry] { sessionLog }
    public func latestKillTiming() -> ScreenShareKillTiming? { killTimings.last }

    // MARK: - Helpers

    private func currentCounts() -> ScreenSharePolicy.SessionCounts {
        var c = 0, v = 0
        for s in sessions.values {
            if s.mode == .control { c += 1 } else if s.mode == .view { v += 1 }
        }
        return .init(controllers: c, viewers: v)
    }

    private func endLog(sessionId: String) {
        let t = now()
        if let idx = sessionLog.lastIndex(where: { $0.sessionId == sessionId && $0.endedAt == nil }) {
            sessionLog[idx].endedAt = t
        }
    }
}
