import CoreGraphics
import Foundation
import os

// MARK: - Allow-list storage

/// The per-phone screen-share allow-list on disk. A phone with no row here is
/// denied: pairing a phone grants it nothing, and the user has to enable the
/// row themselves in Mac settings.
public final class ScreenShareSettingsStore: @unchecked Sendable {
    private let url: URL
    private let lock = NSLock()
    private var loaded = false
    private var rows: [String: ScreenShareDeviceSettings] = [:]

    public init(url: URL) { self.url = url }

    public func all() -> [ScreenShareDeviceSettings] {
        lock.lock(); defer { lock.unlock() }
        load()
        return rows.values.sorted { $0.deviceId < $1.deviceId }
    }

    public func settings(for deviceId: String) -> ScreenShareDeviceSettings? {
        lock.lock(); defer { lock.unlock() }
        load()
        return rows[deviceId]
    }

    /// Writes one row. Throws when the file could not be written, with the old
    /// row put back, so a grant the host cannot remember is never reported as
    /// given.
    public func update(_ settings: ScreenShareDeviceSettings) throws {
        lock.lock(); defer { lock.unlock() }
        load()
        let snapshot = rows
        rows[settings.deviceId] = settings
        do { try save() } catch { rows = snapshot; throw error }
    }

    /// Drops one row. Unlike `update`, a failed write does not put the row
    /// back: taking a grant away must hold in this process even when the file
    /// cannot be written. The error is still thrown so the caller can log it.
    public func remove(_ deviceId: String) throws {
        lock.lock(); defer { lock.unlock() }
        load()
        guard rows[deviceId] != nil else { return }
        rows.removeValue(forKey: deviceId)
        try save()
    }

    /// Clears every row — the pairing key was regenerated, so every phone has
    /// to pair again and starts disabled again. Like `remove`, the rows are gone
    /// in memory even when the write fails.
    public func removeAll() throws {
        lock.lock(); defer { lock.unlock() }
        load()
        guard !rows.isEmpty else { return }
        rows = [:]
        try save()
    }

    private func load() {
        guard !loaded else { return }
        loaded = true
        guard let data = CLIAccountSupport.boundedData(url, maximumBytes: 256 * 1024),
              let decoded = try? JSONDecoder().decode([String: ScreenShareDeviceSettings].self, from: data)
        else { return }
        // A row whose key and id disagree, or a phone id the pairing code would
        // never mint, is dropped rather than trusted with a grant.
        rows = decoded.filter { $0.key == $0.value.deviceId && MobileDeviceRegistry.validClientId($0.key) }
    }

    private func save() throws {
        let data = try JSONEncoder().encode(rows)
        let directory = url.deletingLastPathComponent()
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true,
                                                attributes: [.posixPermissions: 0o700])
        let temporary = directory.appendingPathComponent(url.lastPathComponent + "." + UUID().uuidString)
        var placed = false
        defer { if !placed { try? FileManager.default.removeItem(at: temporary) } }
        guard FileManager.default.createFile(atPath: temporary.path, contents: data,
                                            attributes: [.posixPermissions: 0o600]) else {
            throw MightyError(L("screenShare.error.allowListNotSaved"))
        }
        if FileManager.default.fileExists(atPath: url.path) {
            _ = try FileManager.default.replaceItemAt(url, withItemAt: temporary)
        } else {
            try FileManager.default.moveItem(at: temporary, to: url)
        }
        placed = true
        try? FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
    }
}

// MARK: - What the rest of the host calls

/// The screen-share side of events that happen elsewhere in the app: a phone
/// revoked in settings, a regenerated pairing key. Implemented by
/// `ScreenShareService`; held by `MobileRemoteService` so those paths stop a
/// live session without knowing anything about WebRTC.
public protocol ScreenShareSafetyTarget: Sendable {
    func deviceRevoked(_ deviceId: String) async
    /// `rekeyedAt` is the instant the key rotated: the kill's t0.
    func pairingKeyRegenerated(at rekeyedAt: Date?) async
}

/// What the menu-bar indicator draws.
public struct ScreenShareIndicatorState: Sendable, Equatable {
    public var sessions: [ScreenShareLiveSession]
    /// A controller is connected right now.
    public var controlling: Bool
    /// Frames are stopped because the screen is locked or secure input is on.
    public var framesPaused: Bool
    public var isActive: Bool { !sessions.isEmpty }

    public init(sessions: [ScreenShareLiveSession], controlling: Bool, framesPaused: Bool) {
        self.sessions = sessions; self.controlling = controlling; self.framesPaused = framesPaused
    }
}

// MARK: - Service

/// The production entry point for screen-share safety. Settings, the relay's
/// join requests, the input path, the menu-bar kill switch and the hotkey all
/// come through here, and every one of them ends at `ScreenShareHost`.
public actor ScreenShareService: ScreenShareSafetyTarget {
    private static let log = Logger(subsystem: "dev.mightyclaude.native", category: "screen-share")

    private let store: ScreenShareSettingsStore
    private let host: ScreenShareHost
    private let displays: ScreenShareDisplaySource
    private let input: ScreenShareInputSink
    private let environment: ScreenShareEnvironmentProbe
    private var indicatorObserver: (@Sendable (ScreenShareIndicatorState) -> Void)?
    /// The last state handed to the observer. The lock screen is polled once a
    /// second, so an unchanged state must not redraw the menu-bar item.
    private var publishedState: ScreenShareIndicatorState?
    /// Challenges handed out for a control session that has not started yet.
    /// One challenge admits one attempt: a signature the phone has already used
    /// cannot start a second control session.
    private var pendingChallenges: [String: (challenge: Data, issuedAt: Date)] = [:]
    private var stopObserver: (@Sendable ([ScreenShareStoppedSession]) async -> Void)?
    private var frameBlockObserver: (@Sendable (Bool, ScreenShareStopReason) async -> Void)?
    /// What `refreshEnvironment` last reported, so a transition is announced once.
    private var framesBlocked = false
    /// How long a control challenge stays usable.
    public static let challengeLifetime: TimeInterval = 120

    public init(
        store: ScreenShareSettingsStore,
        host: ScreenShareHost = ScreenShareHost(),
        displays: ScreenShareDisplaySource,
        input: ScreenShareInputSink,
        environment: ScreenShareEnvironmentProbe
    ) {
        self.store = store
        self.host = host
        self.displays = displays
        self.input = input
        self.environment = environment
    }

    /// Told whenever the host stopped sessions by itself — kill switch, revoke,
    /// downgrade, rekey, idle timeout. The engine uses it to close its peers and
    /// send the courtesy note; the host has already stopped everything.
    public func observeStops(_ observer: @escaping @Sendable ([ScreenShareStoppedSession]) async -> Void) {
        stopObserver = observer
    }

    /// Told when the lock screen or secure input started or stopped blocking
    /// frames, with the reason that applies.
    public func observeFrameBlock(_ observer: @escaping @Sendable (Bool, ScreenShareStopReason) async -> Void) {
        frameBlockObserver = observer
    }

    public func observeIndicator(_ observer: @escaping @Sendable (ScreenShareIndicatorState) -> Void) async {
        indicatorObserver = observer
        publishedState = nil
        await publish()
    }

    // MARK: Settings

    public func allowList() -> [ScreenShareDeviceSettings] { store.all() }

    public func settings(for deviceId: String) -> ScreenShareDeviceSettings? { store.settings(for: deviceId) }

    /// Puts a phone on, or takes it off, the allow-list. Taking it off stops
    /// whatever it is doing right now.
    public func setAllowed(deviceId: String, allowed: Bool) async throws {
        var row = store.settings(for: deviceId) ?? ScreenShareDeviceSettings(deviceId: deviceId)
        row.allowed = allowed
        if !allowed { row.grant = .none }
        try store.update(row)
        await host.setDeviceSettings(row)
        if !allowed { await kill(deviceId: deviceId, reason: .revoked) }
        await publish()
    }

    /// Changes one phone's grant. A downgrade — control to view, or either to
    /// none — stops the sessions it no longer covers.
    /// `controlKeyPublicData` is the phone's biometric-gated Keystore public
    /// key, enrolled when control is granted.
    public func setGrant(
        deviceId: String, grant: ScreenShareGrant, controlKeyPublicData: Data? = nil
    ) async throws {
        var row = store.settings(for: deviceId) ?? ScreenShareDeviceSettings(deviceId: deviceId)
        let previous = row.grant
        row.grant = grant
        if grant == .control, let key = controlKeyPublicData { row.controlKeyPublicData = key }
        // A withdrawn control grant takes the enrolled key with it: re-granting
        // control must enrol a fresh biometric key rather than reuse the old one.
        if grant != .control { row.controlKeyPublicData = nil }
        try store.update(row)
        await host.setDeviceSettings(row)
        if grant < previous { await kill(deviceId: deviceId, reason: .grantDowngrade) }
        await publish()
    }

    // MARK: Sessions

    /// A phone asks to view or control. Everything is decided here: the
    /// allow-list, the grant, the control signature and the concurrency limit.
    public func join(
        sessionId: String,
        deviceId: String,
        mode: ScreenShareGrant,
        controlChallenge: Data?,
        controlSignature: Data?,
        surface: ScreenShareSurface
    ) async -> Result<Void, ScreenShareError> {
        // A phone with no clientId shares the legacy row with every other such
        // phone, so it can never be told apart — and never be allow-listed.
        guard deviceId != MobileDeviceRegistry.legacyId else { return .failure(.deviceNotAllowed) }
        if mode == .control {
            // Spend the challenge whether the attempt succeeds or not: the next
            // control start has to ask for a new one and sign it again.
            let issued = pendingChallenges.removeValue(forKey: sessionId)
            guard let issued, let offered = controlChallenge, issued.challenge == offered,
                  Date().timeIntervalSince(issued.issuedAt) < Self.challengeLifetime
            else { return .failure(.controlSignatureInvalid) }
        }
        await refreshEnvironment()
        if let row = store.settings(for: deviceId) {
            await host.setDeviceSettings(row)
        } else {
            await host.forgetDeviceSettings(deviceId)
        }
        let result = await host.requestSession(
            sessionId: sessionId, deviceId: deviceId, requestedMode: mode,
            controlChallenge: controlChallenge, controlSignature: controlSignature,
            surface: surface)
        await publish()
        return result
    }

    /// The challenge the phone's biometric-gated Keystore key signs for this
    /// control session. Fresh every time, remembered until it is used once, so
    /// a captured signature cannot start another control session.
    public func controlChallenge(sessionId: String) -> Data {
        let now = Date()
        pendingChallenges = pendingChallenges.filter { now.timeIntervalSince($0.value.issuedAt) < Self.challengeLifetime }
        let challenge = ScreenSharePolicy.controlChallenge(
            sessionId: sessionId, timestamp: UUID().uuidString)
        pendingChallenges[sessionId] = (challenge, now)
        return challenge
    }

    /// Moves a challenge the phone was handed before it had a session id onto the
    /// session it is actually starting, so `join` spends it under that id and a
    /// signature still cannot be used twice. Nil when there is nothing fresh to
    /// move, which refuses the control start.
    public func adoptChallenge(from key: String, to sessionId: String) -> Data? {
        let now = Date()
        guard let issued = pendingChallenges.removeValue(forKey: key),
              now.timeIntervalSince(issued.issuedAt) < Self.challengeLifetime
        else { return nil }
        pendingChallenges[sessionId] = issued
        return issued.challenge
    }

    /// Drops a challenge nobody is going to use (a start that failed before
    /// `join` reached it).
    public func discardChallenge(sessionId: String) {
        pendingChallenges.removeValue(forKey: sessionId)
    }

    /// Whether the host would admit an injection on this session right now.
    /// The clipboard asks before it reads or writes a pasteboard.
    public func mayInject(sessionId: String) async -> Bool {
        guard await host.mode(of: sessionId) == .control else { return false }
        await refreshEnvironment()
        return await host.canInject()
    }

    /// Remote activity that is not an input event (a clipboard transfer, a zoom)
    /// still keeps the session alive.
    public func noteActivity(sessionId: String) async {
        await host.noteActivity(sessionId: sessionId)
    }

    /// The phone left, or its peer failed.
    public func endSession(sessionId: String, reason: ScreenShareStopReason = .peerLeft) async {
        let before = await host.liveSessions()
        if let timing = await host.endSession(sessionId: sessionId, reason: reason) { record(timing) }
        await announceStops(before: before, reason: reason)
        await publish()
    }

    public func liveSessions() async -> [ScreenShareLiveSession] { await host.liveSessions() }

    public func sessionLog() async -> [ScreenSessionEntry] { await host.log() }

    // MARK: Input

    /// Delivers one remote input event. Nothing reaches `CGEventPost` until the
    /// host has admitted it, and nothing about the event is ever logged.
    @discardableResult
    public func deliver(
        _ event: ScreenShareInputEvent, sessionId: String
    ) async -> Result<ScreenShareResolvedPoint?, ScreenShareInputRejection> {
        guard await host.mode(of: sessionId) == .control else { return .failure(.notControlSession) }
        // The once-a-second poll is too slow for a password field that just took
        // focus: read secure input and the lock screen again for every event.
        await refreshEnvironment()
        guard await host.canInject() else { return .failure(.blocked) }

        switch event {
        case .move(let point):
            guard let resolved = resolve(point) else { return .failure(.noDisplay) }
            await input.move(to: resolved.position, displayId: resolved.displayId)
            await host.noteActivity(sessionId: sessionId)
            return .success(resolved)
        case .click(let point, let button, let clickCount):
            guard let resolved = resolve(point) else { return .failure(.noDisplay) }
            await input.click(at: resolved.position, displayId: resolved.displayId,
                              button: button, clickCount: max(1, min(3, clickCount)))
            await host.noteActivity(sessionId: sessionId)
            return .success(resolved)
        case .drag(let point, let phase):
            guard let resolved = resolve(point) else { return .failure(.noDisplay) }
            await input.drag(at: resolved.position, displayId: resolved.displayId, phase: phase)
            await host.noteActivity(sessionId: sessionId)
            return .success(resolved)
        case .scroll(let point, let deltaX, let deltaY):
            guard let resolved = resolve(point) else { return .failure(.noDisplay) }
            await input.scroll(at: resolved.position, displayId: resolved.displayId,
                               deltaX: deltaX, deltaY: deltaY)
            await host.noteActivity(sessionId: sessionId)
            return .success(resolved)
        case .text(let text):
            await input.commitText(text)
            await host.noteActivity(sessionId: sessionId)
            return .success(nil)
        case .key(let code, let modifiers):
            await input.key(code: code, modifiers: modifiers)
            await host.noteActivity(sessionId: sessionId)
            return .success(nil)
        }
    }

    /// A frame reached the phone: the session is not idle.
    public func noteFrameDelivered(sessionId: String) async {
        await host.noteActivity(sessionId: sessionId)
    }

    private func resolve(_ point: ScreenShareNormalizedPoint) -> ScreenShareResolvedPoint? {
        ScreenShareDisplays.resolve(point: point, displays: displays)
    }

    // MARK: Machine state

    /// Local keyboard or mouse activity on the Mac: remote input pauses for 2 s.
    public func localHIDActivity() async { await host.notifyLocalHID() }

    /// Reads the lock screen and secure-input state and applies it: frames stop
    /// and injection is refused while either holds.
    public func refreshEnvironment() async {
        let locked = environment.screenLocked()
        let secure = environment.secureInputActive()
        await host.setLocked(locked)
        await host.setSecureInput(secure)
        let blocked = locked || secure
        if blocked != framesBlocked {
            framesBlocked = blocked
            // The lock screen wins the reason when both hold: it is the one the
            // person at the Mac can see for themselves.
            await frameBlockObserver?(blocked, locked ? .lockScreen : .secureInput)
        }
        await publish()
    }

    // MARK: Kill triggers

    /// The menu-bar action and the hotkey. Stops every session.
    @discardableResult
    public func killSwitch() async -> ScreenShareKillTiming {
        let before = await host.liveSessions()
        let timing = await host.killAll(reason: .killSwitch)
        record(timing)
        await announceStops(before: before, reason: .killSwitch)
        await publish()
        return timing
    }

    /// A phone was revoked in settings: its row goes, and so does its session.
    public func deviceRevoked(_ deviceId: String) async {
        do { try store.remove(deviceId) } catch {
            Self.log.error("screen-share allow-list row removed in memory but not saved: \(error.localizedDescription, privacy: .public)")
        }
        await host.forgetDeviceSettings(deviceId)
        await kill(deviceId: deviceId, reason: .revoked)
        await publish()
    }

    /// The pairing key was regenerated, so every phone must pair again: every
    /// row goes and every session stops.
    public func pairingKeyRegenerated(at rekeyedAt: Date? = nil) async {
        do { try store.removeAll() } catch {
            Self.log.error("screen-share allow-list cleared in memory but not saved: \(error.localizedDescription, privacy: .public)")
        }
        // Kill before anything else that needs the host, so injection and
        // capture go off at the first hop.
        let before = await host.liveSessions()
        let timing = await host.killAll(reason: .rekeyPairing, triggeredAt: rekeyedAt)
        await host.forgetAllDeviceSettings()
        record(timing)
        await announceStops(before: before, reason: .rekeyPairing)
        await publish()
    }

    private func kill(deviceId: String, reason: ScreenShareStopReason) async {
        let before = await host.liveSessions()
        let timing = await host.killDevice(deviceId: deviceId, reason: reason)
        record(timing)
        await announceStops(before: before, reason: reason)
    }

    /// Diffs the live list around a kill and tells the observer what went. The
    /// host has already stopped capture, injection and the peers by now, so this
    /// is bookkeeping and a courtesy note — never a safety step.
    private func announceStops(before: [ScreenShareLiveSession], reason: ScreenShareStopReason) async {
        guard let stopObserver, !before.isEmpty else { return }
        let after = Set(await host.liveSessions().map(\.sessionId))
        let gone = before.filter { !after.contains($0.sessionId) }
            .map { ScreenShareStoppedSession(sessionId: $0.sessionId, deviceId: $0.deviceId, reason: reason) }
        guard !gone.isEmpty else { return }
        await stopObserver(gone)
    }

    /// t0 at the trigger, t1 when everything stopped — milliseconds on the Mac
    /// clock, and whether a hung surface had to be cut off at the deadline.
    /// Device and reason only: no keystroke ever reaches a log line.
    private func record(_ timing: ScreenShareKillTiming) {
        let milliseconds = Int((timing.elapsed * 1000).rounded())
        if timing.deadlineExceeded {
            Self.log.error("screen-share stop \(String(describing: timing.reason), privacy: .public) took \(milliseconds, privacy: .public) ms and was cut off at the \(Int(ScreenSharePolicy.killDeadline * 1000), privacy: .public) ms deadline")
        } else {
            Self.log.info("screen-share stop \(String(describing: timing.reason), privacy: .public) completed in \(milliseconds, privacy: .public) ms")
        }
    }

    // MARK: Indicator

    public func indicator() async -> ScreenShareIndicatorState {
        let sessions = await host.liveSessions()
        let capturing = await host.isCaptureActive
        return ScreenShareIndicatorState(
            sessions: sessions,
            controlling: sessions.contains { $0.mode == .control },
            framesPaused: !sessions.isEmpty && !capturing)
    }

    private func publish() async {
        guard let observer = indicatorObserver else { return }
        let state = await indicator()
        guard state != publishedState else { return }
        publishedState = state
        observer(state)
    }
}
