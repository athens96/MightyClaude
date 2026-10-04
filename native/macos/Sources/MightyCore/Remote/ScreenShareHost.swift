import Foundation

/// Mac-side enforcement actor for screen-share sessions.
///
/// Every safety decision lives here, and every one of them is taken before the
/// surfaces it controls are touched: capture, injection and the PeerConnection
/// are reached through the injected `ScreenShareSurface`, so the complete logic
/// runs without a display, a relay or a real PeerConnection.
///
/// Covered rules (AC #2):
///   • allow-list off by default; newly paired phones start denied
///   • view and control grants are separate
///   • control requires a valid Keystore-key signature at each session start
///   • revoke / grant-downgrade / pairing-key regen / kill-switch each stop
///     capture, injection and the PeerConnection inside `killDeadline` on the
///     Mac clock — a surface that hangs is cut off at the deadline rather than
///     holding the teardown open
///   • no injection from t0 of any of those triggers onwards
///   • real idle timers: 10 min in control, 30 min in view-only
///   • concurrency: 1 controller + 2 viewers
///   • local HID activity pauses injection for 2 s
///   • lock screen or secure input stops frames and rejects injection
///   • the session log records device, start/end time and mode — never keystrokes
public actor ScreenShareHost {
    // MARK: - Injected dependencies

    /// One step of the teardown or start-up: stop capture, close the peer.
    public typealias CaptureControl = @Sendable () async -> Void
    /// How the idle timers wait. Production sleeps; tests drive their own clock.
    public typealias Sleeper = @Sendable (TimeInterval) async throws -> Void

    // MARK: - Internal session record

    private struct LiveSession: Sendable {
        var deviceId: String
        var mode: ScreenShareGrant
        var startedAt: Date
        var lastActivity: Date
        var surface: ScreenShareSurface
    }

    // MARK: - State

    private var deviceSettings: [String: ScreenShareDeviceSettings] = [:]
    private var sessions: [String: LiveSession] = [:]
    private var sessionLog: [ScreenSessionEntry] = []
    /// The capture surface of the session that started capture. Kept at host
    /// level so the kill switch and the lock screen can stop frames without a
    /// caller handing the closures in again.
    private var captureSurface: ScreenShareSurface?
    private var captureActive = false
    private var injectionEnabled = false
    private var locked = false
    private var secureInputActive = false
    private var hidPausedUntil: Date?
    /// Control-key dialogs open on the Mac right now. While one is, nothing a
    /// phone sends is injected: a remote click must never press "Register".
    private var keyConfirmations = 0
    private var killTimings: [ScreenShareKillTiming] = []
    private var idleTimers: [String: Task<Void, Never>] = [:]
    private let now: @Sendable () -> Date
    private let killDeadline: TimeInterval
    private let idleSleep: Sleeper
    /// Told when an idle timer ended a session, which happens inside the host
    /// with no caller to report back to.
    private var idleObserver: (@Sendable (ScreenShareStoppedSession) async -> Void)?

    // MARK: - Init

    /// - Parameters:
    ///   - now: the Mac clock; t0/t1 and every timeout are read from it.
    ///   - killDeadline: how long a teardown may take before the remaining
    ///     surfaces are cut off. The product value is 1 s.
    ///   - idleSleep: how the idle timers wait.
    public init(
        now: @escaping @Sendable () -> Date = { Date() },
        killDeadline: TimeInterval = ScreenSharePolicy.killDeadline,
        idleSleep: @escaping Sleeper = { seconds in
            try await Task.sleep(nanoseconds: UInt64(max(0, seconds) * 1_000_000_000))
        }
    ) {
        self.now = now
        self.killDeadline = killDeadline
        self.idleSleep = idleSleep
    }

    public func observeIdleStops(_ observer: @escaping @Sendable (ScreenShareStoppedSession) async -> Void) {
        idleObserver = observer
    }

    // MARK: - Allow-list & grants

    public func setDeviceSettings(_ settings: ScreenShareDeviceSettings) {
        deviceSettings[settings.deviceId] = settings
    }

    public func settings(for deviceId: String) -> ScreenShareDeviceSettings? {
        deviceSettings[deviceId]
    }

    public func forgetDeviceSettings(_ deviceId: String) {
        deviceSettings.removeValue(forKey: deviceId)
    }

    public func forgetAllDeviceSettings() {
        deviceSettings = [:]
    }

    // MARK: - Session lifecycle

    /// Evaluates a join request and, if admitted, starts capture and records the session.
    ///
    /// - Parameters:
    ///   - sessionId: caller-chosen opaque identifier for this session.
    ///   - deviceId: the paired phone's clientId.
    ///   - requestedMode: `.view` or `.control`.
    ///   - controlChallenge: the challenge bytes the Mac generated for this session.
    ///   - controlSignature: ECDSA-P256 signature from the phone's biometric-gated
    ///     Keystore key (nil for a view session).
    ///   - surface: capture and peer controls for this session.
    public func requestSession(
        sessionId: String,
        deviceId: String,
        requestedMode: ScreenShareGrant,
        controlChallenge: Data?,
        controlSignature: Data?,
        surface: ScreenShareSurface
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

        guard ScreenSharePolicy.canJoin(mode: requestedMode, counts: currentCounts()) else {
            return .failure(.concurrencyLimit)
        }

        let t = now()
        sessions[sessionId] = LiveSession(
            deviceId: deviceId, mode: requestedMode,
            startedAt: t, lastActivity: t, surface: surface)
        sessionLog.append(ScreenSessionEntry(
            sessionId: sessionId, deviceId: deviceId, mode: requestedMode, startedAt: t))

        if captureSurface == nil { captureSurface = surface }
        // The lock screen and secure input stop frames: a session admitted while
        // either holds starts without capture and picks it up when they clear.
        if !captureActive, !framesBlocked {
            captureActive = true
            await surface.startCapture()
            // The actor is re-entrant, so anything may have happened while
            // capture was starting: a kill that took this session, or a lock
            // screen / password field that must stop the frames. Either way the
            // capture that just came up is unwanted unless capture is still
            // both wanted (`captureActive`) and permitted (`framesBlocked`).
            let wanted = captureActive && !framesBlocked
            if !wanted {
                captureActive = false
                _ = await runWithinDeadline([surface.stopCapture])
            }
            if sessions[sessionId] == nil { return .failure(.sessionStopped) }
        }
        if requestedMode == .control { injectionEnabled = true }
        armIdleTimer(sessionId: sessionId)

        return .success(())
    }

    /// Remote input or a delivered frame keeps the session alive; the idle timer
    /// re-arms from the new activity stamp.
    public func noteActivity(sessionId: String) {
        guard sessions[sessionId] != nil else { return }
        sessions[sessionId]?.lastActivity = now()
    }

    // MARK: - Kill triggers (deadline-enforced)

    /// Kills every live session. `t0` is stamped on entry, `t1` once the
    /// surfaces have stopped or the deadline has cut them off.
    ///
    /// - Parameter triggeredAt: when the trigger happened, if that was before
    ///   this call (a rekey stamps it the instant the key rotated). Defaults to
    ///   now.
    @discardableResult
    public func killAll(reason: ScreenShareStopReason, triggeredAt: Date? = nil) async -> ScreenShareKillTiming {
        let t0 = triggeredAt ?? now()
        // Host state first: from this instant no injection is admitted and no
        // frame is considered live, whatever the surfaces do afterwards.
        injectionEnabled = false
        captureActive = false
        var ops: [CaptureControl] = []
        if let capture = captureSurface { ops.append(capture.stopCapture) }
        for (sid, session) in sessions {
            ops.append(session.surface.closePeer)
            endLog(sessionId: sid)
            cancelIdleTimer(sessionId: sid)
        }
        sessions = [:]
        captureSurface = nil
        return await finishKill(t0: t0, reason: reason, ops: ops)
    }

    /// Kills only the sessions belonging to `deviceId` — revoke, grant
    /// downgrade, or a phone that lost its right to be here.
    @discardableResult
    public func killDevice(deviceId: String, reason: ScreenShareStopReason) async -> ScreenShareKillTiming {
        let t0 = now()
        // Injection is global state here: there is at most one controller, so
        // dropping it costs nothing and closes the window between t0 and the
        // moment the controller's own session is gone.
        injectionEnabled = false
        var ops: [CaptureControl] = []
        for (sid, session) in sessions where session.deviceId == deviceId {
            ops.append(session.surface.closePeer)
            endLog(sessionId: sid)
            cancelIdleTimer(sessionId: sid)
            sessions.removeValue(forKey: sid)
        }
        if sessions.isEmpty {
            captureActive = false
            if let capture = captureSurface { ops.append(capture.stopCapture) }
            captureSurface = nil
        } else {
            // A viewer is still here, so capture stays — but a control session
            // must be re-admitted (and re-signed) before injection returns.
            injectionEnabled = sessions.values.contains { $0.mode == .control }
        }
        return await finishKill(t0: t0, reason: reason, ops: ops)
    }

    /// Ends one session (idle timeout, the phone leaving, a peer failure).
    @discardableResult
    public func endSession(sessionId: String, reason: ScreenShareStopReason) async -> ScreenShareKillTiming? {
        guard let session = sessions[sessionId] else { return nil }
        let t0 = now()
        if session.mode == .control { injectionEnabled = false }
        var ops: [CaptureControl] = [session.surface.closePeer]
        endLog(sessionId: sessionId)
        cancelIdleTimer(sessionId: sessionId)
        sessions.removeValue(forKey: sessionId)
        if sessions.isEmpty {
            captureActive = false
            if let capture = captureSurface { ops.append(capture.stopCapture) }
            captureSurface = nil
        }
        return await finishKill(t0: t0, reason: reason, ops: ops)
    }

    /// Runs the teardown steps and stamps t1. Whatever has not finished by the
    /// deadline is cut off: the host's own state is already safe, and a hung
    /// surface must not keep the user waiting past the promise.
    private func finishKill(
        t0: Date, reason: ScreenShareStopReason, ops: [CaptureControl]
    ) async -> ScreenShareKillTiming {
        let cutOff = await runWithinDeadline(ops)
        let timing = ScreenShareKillTiming(t0: t0, t1: now(), reason: reason, deadlineExceeded: cutOff)
        killTimings.append(timing)
        return timing
    }

    /// Runs every step concurrently and returns whether the deadline cut them
    /// off. The steps run detached so a step that never returns cannot keep
    /// this call — or the actor — suspended beyond the deadline. Steps and
    /// deadline run at high priority: a kill is the user's own (or a safety
    /// rule's) and must not queue behind default-priority work such as agent
    /// output being parsed, or neither the stop nor the cut-off lands in time.
    private func runWithinDeadline(_ ops: [CaptureControl]) async -> Bool {
        guard !ops.isEmpty else { return false }
        let gate = ScreenShareDeadlineGate()
        let work = Task.detached(priority: .high) {
            await withTaskGroup(of: Void.self) { group in
                for op in ops { group.addTask { await op() } }
            }
            gate.finish(timedOut: false)
        }
        let deadline = killDeadline
        let timer = Task.detached(priority: .high) {
            try? await Task.sleep(nanoseconds: UInt64(max(0, deadline) * 1_000_000_000))
            gate.finish(timedOut: true)
        }
        let timedOut = await gate.wait()
        if timedOut { work.cancel() } else { timer.cancel() }
        return timedOut
    }

    // MARK: - Idle timers

    /// Arms (or re-arms) the real idle timer for a session: 10 min in control,
    /// 30 min in view-only. Activity during the wait pushes the deadline out
    /// rather than ending the session.
    private func armIdleTimer(sessionId: String) {
        guard let session = sessions[sessionId] else { return }
        let timeout = ScreenSharePolicy.idleTimeout(for: session.mode)
        let elapsed = now().timeIntervalSince(session.lastActivity)
        let remaining = max(0, timeout - elapsed)
        idleTimers[sessionId]?.cancel()
        idleTimers[sessionId] = Task { [idleSleep] in
            try? await idleSleep(remaining)
            guard !Task.isCancelled else { return }
            await self.idleDeadlineReached(sessionId: sessionId)
        }
    }

    private func cancelIdleTimer(sessionId: String) {
        idleTimers.removeValue(forKey: sessionId)?.cancel()
    }

    /// The timer fired. The session only ends if it really has been idle for
    /// the whole timeout; otherwise the timer is re-armed for the remainder.
    private func idleDeadlineReached(sessionId: String) async {
        guard let session = sessions[sessionId] else { return }
        let timeout = ScreenSharePolicy.idleTimeout(for: session.mode)
        let idleFor = now().timeIntervalSince(session.lastActivity)
        // A clock with sub-millisecond jitter must not postpone the timeout for
        // another full round, so the comparison is made with a small epsilon.
        if idleFor >= timeout - 0.001 {
            _ = await endSession(sessionId: sessionId, reason: .idleTimeout)
            await idleObserver?(ScreenShareStoppedSession(
                sessionId: sessionId, deviceId: session.deviceId, reason: .idleTimeout))
        } else {
            armIdleTimer(sessionId: sessionId)
        }
    }

    // MARK: - Injection gating

    public func canInject() -> Bool {
        guard injectionEnabled, keyConfirmations == 0 else { return false }
        guard !ScreenSharePolicy.injectionBlocked(locked: locked, secureInputActive: secureInputActive) else { return false }
        if let until = hidPausedUntil, now() < until { return false }
        return true
    }

    /// Admits one remote event for a live control session and posts it in the
    /// same actor turn. A kill runs on this actor too, so it either lands
    /// before the check (nothing is posted) or after the post (the post came
    /// before t0) — never in between.
    public func inject(sessionId: String, _ post: @Sendable () -> Void) -> Bool {
        guard sessions[sessionId]?.mode == .control, canInject() else { return false }
        post()
        return true
    }

    /// A control-key dialog opened or closed. Counted, so two phones' dialogs
    /// closing in any order never lift the block early.
    public func beginKeyConfirmation() { keyConfirmations += 1 }
    public func endKeyConfirmation() { keyConfirmations = max(0, keyConfirmations - 1) }

    /// Local HID activity on the Mac: remote injection pauses for 2 s.
    public func notifyLocalHID() {
        hidPausedUntil = now().addingTimeInterval(ScreenSharePolicy.hidPauseDuration)
    }

    // MARK: - Lock screen and secure input

    /// True while frames must not flow at all.
    private var framesBlocked: Bool {
        ScreenSharePolicy.injectionBlocked(locked: locked, secureInputActive: secureInputActive)
    }

    public func setLocked(_ value: Bool) async {
        guard locked != value else { return }
        locked = value
        await applyFrameBlock()
    }

    public func setSecureInput(_ value: Bool) async {
        guard secureInputActive != value else { return }
        secureInputActive = value
        await applyFrameBlock()
    }

    /// Stops frames while the screen is locked or secure input is on, and picks
    /// capture back up once both have cleared. The sessions themselves stay —
    /// the phone is told the state and shows it.
    private func applyFrameBlock() async {
        guard let capture = captureSurface, !sessions.isEmpty else { return }
        if framesBlocked {
            guard captureActive else { return }
            captureActive = false
            _ = await runWithinDeadline([capture.stopCapture])
        } else {
            guard !captureActive else { return }
            captureActive = true
            await capture.startCapture()
            // Re-entrancy again: a kill or a fresh lock that ran while capture came
            // back cleared `captureActive`, so what just started is unwanted.
            if !captureActive { _ = await runWithinDeadline([capture.stopCapture]) }
        }
    }

    // MARK: - Read-only state

    public var sessionCount: Int { sessions.count }
    public var isCaptureActive: Bool { captureActive }
    public var isInjectionEnabled: Bool { injectionEnabled }
    public var isScreenLocked: Bool { locked }
    public var isSecureInputActive: Bool { secureInputActive }
    public func log() -> [ScreenSessionEntry] { sessionLog }
    public func latestKillTiming() -> ScreenShareKillTiming? { killTimings.last }
    public func killTimingHistory() -> [ScreenShareKillTiming] { killTimings }

    /// What the menu-bar indicator draws: one row per live session.
    public func liveSessions() -> [ScreenShareLiveSession] {
        sessions.map { ScreenShareLiveSession(sessionId: $0.key, deviceId: $0.value.deviceId,
                                              mode: $0.value.mode, startedAt: $0.value.startedAt) }
            .sorted { $0.startedAt < $1.startedAt }
    }

    /// The mode a live session runs in, so the input path can refuse a view-only
    /// phone without consulting anything but the host.
    public func mode(of sessionId: String) -> ScreenShareGrant? { sessions[sessionId]?.mode }

    // MARK: - Helpers

    private func currentCounts() -> ScreenSharePolicy.SessionCounts {
        var controllers = 0, viewers = 0
        for session in sessions.values {
            if session.mode == .control { controllers += 1 } else if session.mode == .view { viewers += 1 }
        }
        return .init(controllers: controllers, viewers: viewers)
    }

    private func endLog(sessionId: String) {
        let stamp = now()
        if let index = sessionLog.lastIndex(where: { $0.sessionId == sessionId && $0.endedAt == nil }) {
            sessionLog[index].endedAt = stamp
        }
    }
}

// MARK: - Deadline gate

/// Resumes its waiter exactly once: either the teardown finished, or the
/// deadline passed. A lock rather than an actor, because the deadline must be
/// readable from a detached task that is deliberately outside the host.
final class ScreenShareDeadlineGate: @unchecked Sendable {
    private let lock = NSLock()
    private var continuation: CheckedContinuation<Bool, Never>?
    private var settled: Bool?

    /// Returns true when the deadline won the race.
    func wait() async -> Bool {
        await withCheckedContinuation { (c: CheckedContinuation<Bool, Never>) in
            lock.lock()
            if let settled {
                lock.unlock()
                c.resume(returning: settled)
                return
            }
            continuation = c
            lock.unlock()
        }
    }

    func finish(timedOut: Bool) {
        lock.lock()
        guard settled == nil else { lock.unlock(); return }
        settled = timedOut
        let waiter = continuation
        continuation = nil
        lock.unlock()
        waiter?.resume(returning: timedOut)
    }
}
