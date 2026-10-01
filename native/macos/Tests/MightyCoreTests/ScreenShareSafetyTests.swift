import CoreGraphics
import CryptoKit
import Foundation
import Testing
@testable import MightyCore

/// Proves every host-enforced safety rule for AC #2.
/// Uses a scripted fake peer — no relay, no display, no real capture subsystem.
struct ScreenShareSafetyTests {

    // MARK: - Fake peer helpers

    private func fakeOp() -> ScreenShareHost.CaptureControl { { @Sendable in } }

    private func isSuccess(_ r: Result<Void, ScreenShareError>) -> Bool {
        if case .success = r { return true }; return false
    }
    private func isFailure(_ r: Result<Void, ScreenShareError>, _ expected: ScreenShareError) -> Bool {
        if case .failure(let e) = r { return e == expected }; return false
    }

    /// A P-256 key pair for control-session biometric verification tests.
    private struct FakeKeystore {
        let privateKey = P256.Signing.PrivateKey()
        var publicKeyData: Data { privateKey.publicKey.x963Representation }

        func sign(challenge: Data) -> Data {
            (try? privateKey.signature(for: SHA256.hash(data: challenge)).derRepresentation) ?? Data()
        }
    }

    // MARK: - 1. Allow-list: off by default for newly paired phones

    @Test func newlyPairedPhoneIsNotAllowed() async {
        let host = ScreenShareHost()
        // No settings entry at all — denied.
        let result = await host.requestSession(
            sessionId: "s1", deviceId: "phone-new", requestedMode: .view,
            controlChallenge: nil, controlSignature: nil,
            startCapture: fakeOp(), peerClose: fakeOp())
        #expect(isFailure(result, .deviceNotAllowed))
        #expect(await host.sessionCount == 0)
    }

    @Test func phoneWithSettingsAllowedFalseIsDenied() async {
        let host = ScreenShareHost()
        await host.setDeviceSettings(ScreenShareDeviceSettings(deviceId: "p1", allowed: false, grant: .view))
        let result = await host.requestSession(
            sessionId: "s1", deviceId: "p1", requestedMode: .view,
            controlChallenge: nil, controlSignature: nil,
            startCapture: fakeOp(), peerClose: fakeOp())
        #expect(isFailure(result, .deviceNotAllowed))
    }

    @Test func phoneEnabledInAllowListCanJoin() async {
        let host = ScreenShareHost()
        await host.setDeviceSettings(ScreenShareDeviceSettings(deviceId: "p1", allowed: true, grant: .view))
        let result = await host.requestSession(
            sessionId: "s1", deviceId: "p1", requestedMode: .view,
            controlChallenge: nil, controlSignature: nil,
            startCapture: fakeOp(), peerClose: fakeOp())
        #expect(isSuccess(result))
    }

    // MARK: - 2. View and control grants are separate

    @Test func viewGrantDoesNotAllowControl() async {
        let host = ScreenShareHost()
        await host.setDeviceSettings(ScreenShareDeviceSettings(deviceId: "p1", allowed: true, grant: .view))
        let result = await host.requestSession(
            sessionId: "s1", deviceId: "p1", requestedMode: .control,
            controlChallenge: nil, controlSignature: nil,
            startCapture: fakeOp(), peerClose: fakeOp())
        #expect(isFailure(result, .insufficientGrant))
    }

    @Test func viewGrantAllowsViewMode() async {
        let host = ScreenShareHost()
        await host.setDeviceSettings(ScreenShareDeviceSettings(deviceId: "p1", allowed: true, grant: .view))
        let r = await host.requestSession(
            sessionId: "s1", deviceId: "p1", requestedMode: .view,
            controlChallenge: nil, controlSignature: nil,
            startCapture: fakeOp(), peerClose: fakeOp())
        #expect(isSuccess(r))
    }

    @Test func controlGrantAllowsBothModes() async {
        let ks = FakeKeystore()
        let host = ScreenShareHost()
        await host.setDeviceSettings(ScreenShareDeviceSettings(
            deviceId: "p1", allowed: true, grant: .control, controlKeyPublicData: ks.publicKeyData))
        let challenge = ScreenSharePolicy.controlChallenge(sessionId: "s1", timestamp: "t1")
        let sig = ks.sign(challenge: challenge)

        let viewResult = await host.requestSession(
            sessionId: "sv", deviceId: "p1", requestedMode: .view,
            controlChallenge: nil, controlSignature: nil,
            startCapture: fakeOp(), peerClose: fakeOp())
        #expect(isSuccess(viewResult))

        let controlResult = await host.requestSession(
            sessionId: "s1", deviceId: "p1", requestedMode: .control,
            controlChallenge: challenge, controlSignature: sig,
            startCapture: fakeOp(), peerClose: fakeOp())
        #expect(isSuccess(controlResult))
    }

    @Test func noGrantDeniesAll() {
        #expect(!ScreenSharePolicy.grantAllows(grant: .none, requestedMode: .view))
        #expect(!ScreenSharePolicy.grantAllows(grant: .none, requestedMode: .control))
        #expect(ScreenSharePolicy.grantAllows(grant: .none, requestedMode: .none))
    }

    // MARK: - 3. Control requires valid Keystore-key signature

    @Test func controlSessionRequiresSignature() async {
        let ks = FakeKeystore()
        let host = ScreenShareHost()
        await host.setDeviceSettings(ScreenShareDeviceSettings(
            deviceId: "p1", allowed: true, grant: .control, controlKeyPublicData: ks.publicKeyData))

        // No signature at all
        let r1 = await host.requestSession(
            sessionId: "s1", deviceId: "p1", requestedMode: .control,
            controlChallenge: nil, controlSignature: nil,
            startCapture: fakeOp(), peerClose: fakeOp())
        #expect(isFailure(r1, .controlSignatureInvalid))

        // Wrong key
        let wrongKs = FakeKeystore()
        let challenge = ScreenSharePolicy.controlChallenge(sessionId: "s1", timestamp: "ts")
        let wrongSig = wrongKs.sign(challenge: challenge)
        let r2 = await host.requestSession(
            sessionId: "s1", deviceId: "p1", requestedMode: .control,
            controlChallenge: challenge, controlSignature: wrongSig,
            startCapture: fakeOp(), peerClose: fakeOp())
        #expect(isFailure(r2, .controlSignatureInvalid))
    }

    @Test func controlSessionWithValidSignatureSucceeds() async {
        let ks = FakeKeystore()
        let host = ScreenShareHost()
        await host.setDeviceSettings(ScreenShareDeviceSettings(
            deviceId: "p1", allowed: true, grant: .control, controlKeyPublicData: ks.publicKeyData))
        let challenge = ScreenSharePolicy.controlChallenge(sessionId: "ctrl-1", timestamp: "2026-10-01T00:00:00Z")
        let sig = ks.sign(challenge: challenge)
        let r = await host.requestSession(
            sessionId: "ctrl-1", deviceId: "p1", requestedMode: .control,
            controlChallenge: challenge, controlSignature: sig,
            startCapture: fakeOp(), peerClose: fakeOp())
        #expect(isSuccess(r))
        #expect(await host.isInjectionEnabled)
    }

    // MARK: - 4. Kill timing: t1 − t0 ≤ 1 s, no injection after t0

    @Test func killAllStopsWithinOneSecond() async {
        let host = ScreenShareHost()
        await host.setDeviceSettings(ScreenShareDeviceSettings(deviceId: "p1", allowed: true, grant: .view))
        _ = await host.requestSession(
            sessionId: "s1", deviceId: "p1", requestedMode: .view,
            controlChallenge: nil, controlSignature: nil,
            startCapture: fakeOp(), peerClose: fakeOp())
        #expect(await host.isCaptureActive)
        await host.killAll(reason: .killSwitch, stopCapture: fakeOp())
        let timing = await host.latestKillTiming()
        #expect(timing != nil)
        #expect(timing!.elapsed <= ScreenSharePolicy.killDeadline)
        #expect(await host.sessionCount == 0)
        #expect(await host.isCaptureActive == false)
    }

    @Test func noInjectionAfterKillT0() async {
        let ks = FakeKeystore()
        let host = ScreenShareHost()
        await host.setDeviceSettings(ScreenShareDeviceSettings(
            deviceId: "p1", allowed: true, grant: .control, controlKeyPublicData: ks.publicKeyData))
        let challenge = ScreenSharePolicy.controlChallenge(sessionId: "s1", timestamp: "ts")
        let sig = ks.sign(challenge: challenge)
        _ = await host.requestSession(
            sessionId: "s1", deviceId: "p1", requestedMode: .control,
            controlChallenge: challenge, controlSignature: sig,
            startCapture: fakeOp(), peerClose: fakeOp())
        #expect(await host.canInject())

        await host.killAll(reason: .killSwitch, stopCapture: fakeOp())
        // Injection disabled — no injection after t0.
        #expect(await host.canInject() == false)
    }

    @Test func revokeDeviceStopsWithinOneSecond() async {
        let host = ScreenShareHost()
        await host.setDeviceSettings(ScreenShareDeviceSettings(deviceId: "p1", allowed: true, grant: .view))
        _ = await host.requestSession(
            sessionId: "s1", deviceId: "p1", requestedMode: .view,
            controlChallenge: nil, controlSignature: nil,
            startCapture: fakeOp(), peerClose: fakeOp())
        await host.killDevice(deviceId: "p1", reason: .revoked, stopCapture: fakeOp())
        let timing = await host.latestKillTiming()!
        #expect(timing.elapsed <= ScreenSharePolicy.killDeadline)
        #expect(await host.sessionCount == 0)
        #expect(await host.isCaptureActive == false)
    }

    @Test func grantDowngradeStopsWithinOneSecond() async {
        let ks = FakeKeystore()
        let host = ScreenShareHost()
        await host.setDeviceSettings(ScreenShareDeviceSettings(
            deviceId: "p1", allowed: true, grant: .control, controlKeyPublicData: ks.publicKeyData))
        let challenge = ScreenSharePolicy.controlChallenge(sessionId: "s1", timestamp: "ts")
        let sig = ks.sign(challenge: challenge)
        _ = await host.requestSession(
            sessionId: "s1", deviceId: "p1", requestedMode: .control,
            controlChallenge: challenge, controlSignature: sig,
            startCapture: fakeOp(), peerClose: fakeOp())
        // Grant downgraded to view: kill the control session
        await host.killDevice(deviceId: "p1", reason: .grantDowngrade, stopCapture: fakeOp())
        let timing = await host.latestKillTiming()!
        #expect(timing.elapsed <= ScreenSharePolicy.killDeadline)
        #expect(await host.canInject() == false)
    }

    @Test func rekeyPairingStopsWithinOneSecond() async {
        let host = ScreenShareHost()
        await host.setDeviceSettings(ScreenShareDeviceSettings(deviceId: "p1", allowed: true, grant: .view))
        _ = await host.requestSession(
            sessionId: "s1", deviceId: "p1", requestedMode: .view,
            controlChallenge: nil, controlSignature: nil,
            startCapture: fakeOp(), peerClose: fakeOp())
        await host.killAll(reason: .rekeyPairing, stopCapture: fakeOp())
        let timing = await host.latestKillTiming()!
        #expect(timing.elapsed <= ScreenSharePolicy.killDeadline)
        #expect(await host.sessionCount == 0)
    }

    // MARK: - 5. Idle timeouts

    @Test func controlIdleTimeoutIsConfiguredCorrectly() {
        #expect(ScreenSharePolicy.idleTimeout(for: .control) == 600)   // 10 min
    }

    @Test func viewIdleTimeoutIsConfiguredCorrectly() {
        #expect(ScreenSharePolicy.idleTimeout(for: .view) == 1800)      // 30 min
    }

    @Test func idleTimeoutEndsTHeSession() async {
        let host = ScreenShareHost()
        await host.setDeviceSettings(ScreenShareDeviceSettings(deviceId: "p1", allowed: true, grant: .view))
        _ = await host.requestSession(
            sessionId: "s1", deviceId: "p1", requestedMode: .view,
            controlChallenge: nil, controlSignature: nil,
            startCapture: fakeOp(), peerClose: fakeOp())
        #expect(await host.sessionCount == 1)
        await host.triggerIdleTimeout(sessionId: "s1", stopCapture: fakeOp())
        #expect(await host.sessionCount == 0)
        #expect(await host.isCaptureActive == false)
        let timing = await host.latestKillTiming()!
        #expect(timing.elapsed <= ScreenSharePolicy.killDeadline)
    }

    // MARK: - 6. Concurrency: 1 controller + 2 viewers

    @Test func atMostOneControllerAdmitted() async {
        let ks1 = FakeKeystore(); let ks2 = FakeKeystore()
        let host = ScreenShareHost()
        for id in ["p1", "p2"] {
            let ks = id == "p1" ? ks1 : ks2
            await host.setDeviceSettings(ScreenShareDeviceSettings(
                deviceId: id, allowed: true, grant: .control, controlKeyPublicData: ks.publicKeyData))
        }
        let ch1 = ScreenSharePolicy.controlChallenge(sessionId: "c1", timestamp: "t1")
        _ = await host.requestSession(
            sessionId: "c1", deviceId: "p1", requestedMode: .control,
            controlChallenge: ch1, controlSignature: ks1.sign(challenge: ch1),
            startCapture: fakeOp(), peerClose: fakeOp())
        let ch2 = ScreenSharePolicy.controlChallenge(sessionId: "c2", timestamp: "t2")
        let r = await host.requestSession(
            sessionId: "c2", deviceId: "p2", requestedMode: .control,
            controlChallenge: ch2, controlSignature: ks2.sign(challenge: ch2),
            startCapture: fakeOp(), peerClose: fakeOp())
        #expect(isFailure(r, .concurrencyLimit))
    }

    @Test func atMostTwoViewersAdmitted() async {
        let host = ScreenShareHost()
        for id in ["v1", "v2", "v3"] {
            await host.setDeviceSettings(ScreenShareDeviceSettings(deviceId: id, allowed: true, grant: .view))
        }
        for (n, id) in ["v1", "v2"].enumerated() {
            let r = await host.requestSession(
                sessionId: "s\(n)", deviceId: id, requestedMode: .view,
                controlChallenge: nil, controlSignature: nil,
                startCapture: fakeOp(), peerClose: fakeOp())
            #expect(isSuccess(r))
        }
        let r3 = await host.requestSession(
            sessionId: "s3", deviceId: "v3", requestedMode: .view,
            controlChallenge: nil, controlSignature: nil,
            startCapture: fakeOp(), peerClose: fakeOp())
        #expect(isFailure(r3, .concurrencyLimit))
    }

    @Test func canJoinConcurrencyRules() {
        #expect(ScreenSharePolicy.canJoin(mode: .control, counts: .init(controllers: 0, viewers: 0)))
        #expect(!ScreenSharePolicy.canJoin(mode: .control, counts: .init(controllers: 1, viewers: 0)))
        #expect(ScreenSharePolicy.canJoin(mode: .view, counts: .init(controllers: 0, viewers: 1)))
        #expect(!ScreenSharePolicy.canJoin(mode: .view, counts: .init(controllers: 0, viewers: 2)))
    }

    // MARK: - 7. Local HID pauses injection for 2 s

    @Test func hidPausesInjectionForTwoSeconds() async {
        let ks = FakeKeystore()
        let clock = TestClock(Date(timeIntervalSinceReferenceDate: 0))
        let host = ScreenShareHost(now: { clock.date })
        await host.setDeviceSettings(ScreenShareDeviceSettings(
            deviceId: "p1", allowed: true, grant: .control, controlKeyPublicData: ks.publicKeyData))
        let challenge = ScreenSharePolicy.controlChallenge(sessionId: "s1", timestamp: "ts")
        _ = await host.requestSession(
            sessionId: "s1", deviceId: "p1", requestedMode: .control,
            controlChallenge: challenge, controlSignature: ks.sign(challenge: challenge),
            startCapture: fakeOp(), peerClose: fakeOp())
        #expect(await host.canInject())

        await host.notifyLocalHID()
        // Still within the 2 s pause (1.5 s after HID)
        clock.advance(by: 1.5)
        #expect(await host.canInject() == false)
        // After 2 s the pause lifts (total 2.2 s after HID)
        clock.advance(by: 0.7)
        #expect(await host.canInject())
    }

    // MARK: - 8. Lock screen and secure input block injection

    @Test func lockScreenBlocksInjection() async {
        let ks = FakeKeystore()
        let host = ScreenShareHost()
        await host.setDeviceSettings(ScreenShareDeviceSettings(
            deviceId: "p1", allowed: true, grant: .control, controlKeyPublicData: ks.publicKeyData))
        let challenge = ScreenSharePolicy.controlChallenge(sessionId: "s1", timestamp: "ts")
        _ = await host.requestSession(
            sessionId: "s1", deviceId: "p1", requestedMode: .control,
            controlChallenge: challenge, controlSignature: ks.sign(challenge: challenge),
            startCapture: fakeOp(), peerClose: fakeOp())
        #expect(await host.canInject())

        await host.setLocked(true)
        #expect(await host.canInject() == false)
        await host.setLocked(false)
        #expect(await host.canInject())
    }

    @Test func secureInputBlocksInjection() async {
        let ks = FakeKeystore()
        let host = ScreenShareHost()
        await host.setDeviceSettings(ScreenShareDeviceSettings(
            deviceId: "p1", allowed: true, grant: .control, controlKeyPublicData: ks.publicKeyData))
        let challenge = ScreenSharePolicy.controlChallenge(sessionId: "s1", timestamp: "ts")
        _ = await host.requestSession(
            sessionId: "s1", deviceId: "p1", requestedMode: .control,
            controlChallenge: challenge, controlSignature: ks.sign(challenge: challenge),
            startCapture: fakeOp(), peerClose: fakeOp())
        #expect(await host.canInject())

        await host.setSecureInput(true)
        #expect(await host.canInject() == false)
        await host.setSecureInput(false)
        #expect(await host.canInject())
    }

    @Test func injectionBlockedPolicyFunction() {
        #expect(ScreenSharePolicy.injectionBlocked(locked: true, secureInputActive: false))
        #expect(ScreenSharePolicy.injectionBlocked(locked: false, secureInputActive: true))
        #expect(ScreenSharePolicy.injectionBlocked(locked: true, secureInputActive: true))
        #expect(!ScreenSharePolicy.injectionBlocked(locked: false, secureInputActive: false))
    }

    // MARK: - 9. Session log has no keystrokes

    @Test func sessionLogContainsOnlyAllowedFields() async {
        let host = ScreenShareHost()
        await host.setDeviceSettings(ScreenShareDeviceSettings(deviceId: "p1", allowed: true, grant: .view))
        _ = await host.requestSession(
            sessionId: "s1", deviceId: "p1", requestedMode: .view,
            controlChallenge: nil, controlSignature: nil,
            startCapture: fakeOp(), peerClose: fakeOp())
        await host.killAll(reason: .killSwitch, stopCapture: fakeOp())

        let entries = await host.log()
        #expect(entries.count == 1)
        let entry = entries[0]
        // The entry only has: sessionId, deviceId, mode, startedAt, endedAt.
        // There is no keystroke field — the type enforces this by construction.
        #expect(entry.deviceId == "p1")
        #expect(entry.mode == .view)
        #expect(entry.endedAt != nil)
        // ScreenSessionEntry has no `keystrokes` property — verified at compile time.
        // Confirm the Mirror shows only the expected stored properties.
        let props = Set(Mirror(reflecting: entry).children.compactMap(\.label))
        #expect(!props.contains("keystrokes"))
        #expect(!props.contains("text"))
        #expect(!props.contains("keys"))
        #expect(props.contains("sessionId"))
        #expect(props.contains("deviceId"))
        #expect(props.contains("mode"))
        #expect(props.contains("startedAt"))
        #expect(props.contains("endedAt"))
    }

    // MARK: - 10. Normalized display coordinates map to CGDisplayBounds

    @Test func normalizedCoordinatesMapToDisplayBounds() {
        let bounds = CGRect(x: 100, y: 200, width: 1920, height: 1080)

        let topLeft = ScreenShareNormalizedPoint(displayId: 1, x: 0, y: 0)
        let mapped0 = ScreenSharePolicy.map(point: topLeft, bounds: bounds)
        #expect(mapped0.x == 100 && mapped0.y == 200)

        let bottomRight = ScreenShareNormalizedPoint(displayId: 1, x: 1, y: 1)
        let mapped1 = ScreenSharePolicy.map(point: bottomRight, bounds: bounds)
        #expect(mapped1.x == 2020 && mapped1.y == 1280)

        let center = ScreenShareNormalizedPoint(displayId: 1, x: 0.5, y: 0.5)
        let mappedC = ScreenSharePolicy.map(point: center, bounds: bounds)
        #expect(mappedC.x == 100 + 960 && mappedC.y == 200 + 540)

        // Out-of-bounds values are clamped.
        let overshot = ScreenShareNormalizedPoint(displayId: 1, x: 1.5, y: -0.1)
        let mappedO = ScreenSharePolicy.map(point: overshot, bounds: bounds)
        #expect(mappedO.x == 100 + 1920 && mappedO.y == 200 + 0)
    }

    @Test func displayIdIsPreservedInNormalizedPoint() {
        let pt = ScreenShareNormalizedPoint(displayId: 42, x: 0.25, y: 0.75)
        #expect(pt.displayId == 42)
    }

    // MARK: - Bonus: policy pure-logic coverage

    @Test func idleTimeoutValuesMatchSpec() {
        #expect(ScreenSharePolicy.controlIdleTimeout == 600)
        #expect(ScreenSharePolicy.viewIdleTimeout == 1800)
    }

    @Test func maxConcurrencyConstants() {
        #expect(ScreenSharePolicy.maxControllers == 1)
        #expect(ScreenSharePolicy.maxViewers == 2)
    }

    @Test func hidPauseDurationIsTwo() {
        #expect(ScreenSharePolicy.hidPauseDuration == 2)
    }

    @Test func killDeadlineIsOneSecond() {
        #expect(ScreenSharePolicy.killDeadline == 1)
    }
}

// MARK: - Sendable test clock

/// A mutable clock whose `date` can be advanced from test code.
/// `@unchecked Sendable` is safe here because tests are single-threaded.
private final class TestClock: @unchecked Sendable {
    private(set) var date: Date
    init(_ date: Date) { self.date = date }
    func advance(by seconds: TimeInterval) { date = date.addingTimeInterval(seconds) }
}
