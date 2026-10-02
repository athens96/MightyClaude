import CoreGraphics
import CryptoKit
import Foundation
import Testing
@testable import MightyCore

/// Proves every host-enforced screen-share safety rule (AC #2) with a scripted
/// fake peer: no relay, no display, no real capture stream, no PeerConnection.
struct ScreenShareSafetyTests {

    // MARK: - Scripted fake peer

    /// The phone's side of a session, scripted. It records the order the host
    /// stopped things in, can pretend the relay is dead, can hang its peer
    /// close forever, and probes whether the host would admit injection while
    /// the teardown is in flight — that is, after t0.
    final class ScriptedFakePeer: @unchecked Sendable {
        private let lock = NSLock()
        /// With the relay down every relay-bound notification fails; teardown
        /// must still finish locally.
        var relayUp = true
        /// A peer whose close never returns. The host must cut it off.
        var closeHangs = false
        private var log: [String] = []
        private var injectionProbes = 0
        /// Asked during the teardown, so the answer is the host's answer after t0.
        var injectionProbe: (@Sendable () async -> Bool)?

        var events: [String] { lock.lock(); defer { lock.unlock() }; return log }
        var injectionsAfterT0: Int { lock.lock(); defer { lock.unlock() }; return injectionProbes }

        private func record(_ event: String) { lock.lock(); log.append(event); lock.unlock() }
        private func countInjection() { lock.lock(); injectionProbes += 1; lock.unlock() }

        func surface() -> ScreenShareSurface {
            ScreenShareSurface(
                startCapture: { @Sendable in self.record("capture-started") },
                stopCapture: { @Sendable in self.record("capture-stopped") },
                closePeer: { @Sendable in
                    if !self.relayUp { self.record("relay-send-failed") }
                    if let probe = self.injectionProbe, await probe() { self.countInjection() }
                    if self.closeHangs {
                        // A peer that never finishes closing: the host's deadline,
                        // not this closure, decides when the teardown is over.
                        self.record("peer-close-hung")
                        try? await Task.sleep(nanoseconds: 60_000_000_000)
                        // Reached only because the host cut the hang off; without
                        // the deadline this would be a 60 s wait.
                        self.record(Task.isCancelled ? "peer-close-cut-off" : "peer-close-returned-late")
                        return
                    }
                    self.record("peer-closed")
                })
        }
    }

    /// A P-256 key pair standing in for the phone's biometric-gated Android
    /// Keystore signing key.
    struct FakeKeystore {
        let privateKey = P256.Signing.PrivateKey()
        var publicKeyData: Data { privateKey.publicKey.x963Representation }
        func sign(challenge: Data) -> Data {
            (try? privateKey.signature(for: SHA256.hash(data: challenge)).derRepresentation) ?? Data()
        }
    }

    private func quietSurface() -> ScreenShareSurface {
        ScreenShareSurface(startCapture: {}, stopCapture: {}, closePeer: {})
    }

    private func isSuccess(_ result: Result<Void, ScreenShareError>) -> Bool {
        if case .success = result { return true }; return false
    }
    private func isFailure(_ result: Result<Void, ScreenShareError>, _ expected: ScreenShareError) -> Bool {
        if case .failure(let error) = result { return error == expected }; return false
    }

    /// Builds a host with one live control session, signed by a real key pair.
    private func hostWithControlSession(
        host: ScreenShareHost = ScreenShareHost(),
        deviceId: String = "p1", sessionId: String = "s1",
        surface: ScreenShareSurface
    ) async -> ScreenShareHost {
        let keystore = FakeKeystore()
        await host.setDeviceSettings(ScreenShareDeviceSettings(
            deviceId: deviceId, allowed: true, grant: .control,
            controlKeyPublicData: keystore.publicKeyData))
        let challenge = ScreenSharePolicy.controlChallenge(sessionId: sessionId, timestamp: "ts")
        _ = await host.requestSession(
            sessionId: sessionId, deviceId: deviceId, requestedMode: .control,
            controlChallenge: challenge, controlSignature: keystore.sign(challenge: challenge),
            surface: surface)
        return host
    }

    // MARK: - 1. Allow-list: off by default for newly paired phones

    @Test func newlyPairedPhoneIsNotAllowed() async {
        let host = ScreenShareHost()
        // No row at all — a phone that has just paired is denied.
        let result = await host.requestSession(
            sessionId: "s1", deviceId: "phone-new", requestedMode: .view,
            controlChallenge: nil, controlSignature: nil, surface: quietSurface())
        #expect(isFailure(result, .deviceNotAllowed))
        #expect(await host.sessionCount == 0)
        #expect(await host.isCaptureActive == false)
    }

    @Test func phoneWithAllowedFalseIsDenied() async {
        let host = ScreenShareHost()
        await host.setDeviceSettings(ScreenShareDeviceSettings(deviceId: "p1", allowed: false, grant: .view))
        let result = await host.requestSession(
            sessionId: "s1", deviceId: "p1", requestedMode: .view,
            controlChallenge: nil, controlSignature: nil, surface: quietSurface())
        #expect(isFailure(result, .deviceNotAllowed))
    }

    @Test func phoneEnabledInAllowListCanJoin() async {
        let peer = ScriptedFakePeer()
        let host = ScreenShareHost()
        await host.setDeviceSettings(ScreenShareDeviceSettings(deviceId: "p1", allowed: true, grant: .view))
        let result = await host.requestSession(
            sessionId: "s1", deviceId: "p1", requestedMode: .view,
            controlChallenge: nil, controlSignature: nil, surface: peer.surface())
        #expect(isSuccess(result))
        #expect(await host.sessionCount == 1)
        #expect(await host.isCaptureActive)
        #expect(peer.events == ["capture-started"])
        // A view session never enables injection.
        #expect(await host.canInject() == false)
    }

    @Test func defaultSettingsAreOffAndWithoutGrant() {
        let fresh = ScreenShareDeviceSettings(deviceId: "p1")
        #expect(fresh.allowed == false)
        #expect(fresh.grant == .none)
        #expect(fresh.controlKeyPublicData == nil)
        #expect(ScreenSharePolicy.isAllowed(nil) == false)
        #expect(ScreenSharePolicy.isAllowed(fresh) == false)
    }

    // MARK: - 2. View and control grants are separate

    @Test func viewGrantDoesNotAllowControl() async {
        let keystore = FakeKeystore()
        let host = ScreenShareHost()
        await host.setDeviceSettings(ScreenShareDeviceSettings(
            deviceId: "p1", allowed: true, grant: .view, controlKeyPublicData: keystore.publicKeyData))
        let challenge = ScreenSharePolicy.controlChallenge(sessionId: "s1", timestamp: "ts")
        let result = await host.requestSession(
            sessionId: "s1", deviceId: "p1", requestedMode: .control,
            controlChallenge: challenge, controlSignature: keystore.sign(challenge: challenge),
            surface: quietSurface())
        #expect(isFailure(result, .insufficientGrant))
        #expect(await host.isInjectionEnabled == false)
    }

    @Test func controlGrantAlsoAllowsView() async {
        let host = ScreenShareHost()
        await host.setDeviceSettings(ScreenShareDeviceSettings(deviceId: "p1", allowed: true, grant: .control))
        let result = await host.requestSession(
            sessionId: "s1", deviceId: "p1", requestedMode: .view,
            controlChallenge: nil, controlSignature: nil, surface: quietSurface())
        #expect(isSuccess(result))
        #expect(await host.isInjectionEnabled == false)
    }

    @Test func noGrantAllowsNeither() async {
        let host = ScreenShareHost()
        await host.setDeviceSettings(ScreenShareDeviceSettings(deviceId: "p1", allowed: true, grant: .none))
        #expect(isFailure(await host.requestSession(
            sessionId: "s1", deviceId: "p1", requestedMode: .view,
            controlChallenge: nil, controlSignature: nil, surface: quietSurface()), .insufficientGrant))
        #expect(isFailure(await host.requestSession(
            sessionId: "s2", deviceId: "p1", requestedMode: .control,
            controlChallenge: nil, controlSignature: nil, surface: quietSurface()), .insufficientGrant))
    }

    @Test func grantRulesAreSeparate() {
        #expect(ScreenSharePolicy.grantAllows(grant: .view, requestedMode: .view))
        #expect(!ScreenSharePolicy.grantAllows(grant: .view, requestedMode: .control))
        #expect(ScreenSharePolicy.grantAllows(grant: .control, requestedMode: .view))
        #expect(ScreenSharePolicy.grantAllows(grant: .control, requestedMode: .control))
        #expect(!ScreenSharePolicy.grantAllows(grant: .none, requestedMode: .view))
        #expect(!ScreenSharePolicy.grantAllows(grant: .none, requestedMode: .control))
    }

    // MARK: - 3. Control requires a valid Keystore-key signature

    @Test func controlWithoutSignatureIsRefused() async {
        let keystore = FakeKeystore()
        let host = ScreenShareHost()
        await host.setDeviceSettings(ScreenShareDeviceSettings(
            deviceId: "p1", allowed: true, grant: .control, controlKeyPublicData: keystore.publicKeyData))
        let result = await host.requestSession(
            sessionId: "s1", deviceId: "p1", requestedMode: .control,
            controlChallenge: ScreenSharePolicy.controlChallenge(sessionId: "s1", timestamp: "ts"),
            controlSignature: nil, surface: quietSurface())
        #expect(isFailure(result, .controlSignatureInvalid))
    }

    @Test func controlWithAnotherKeysSignatureIsRefused() async {
        let enrolled = FakeKeystore(), attacker = FakeKeystore()
        let host = ScreenShareHost()
        await host.setDeviceSettings(ScreenShareDeviceSettings(
            deviceId: "p1", allowed: true, grant: .control, controlKeyPublicData: enrolled.publicKeyData))
        let challenge = ScreenSharePolicy.controlChallenge(sessionId: "s1", timestamp: "ts")
        let result = await host.requestSession(
            sessionId: "s1", deviceId: "p1", requestedMode: .control,
            controlChallenge: challenge, controlSignature: attacker.sign(challenge: challenge),
            surface: quietSurface())
        #expect(isFailure(result, .controlSignatureInvalid))
        #expect(await host.isInjectionEnabled == false)
    }

    @Test func controlSignatureOverAnotherSessionsChallengeIsRefused() async {
        let keystore = FakeKeystore()
        let host = ScreenShareHost()
        await host.setDeviceSettings(ScreenShareDeviceSettings(
            deviceId: "p1", allowed: true, grant: .control, controlKeyPublicData: keystore.publicKeyData))
        let other = ScreenSharePolicy.controlChallenge(sessionId: "other", timestamp: "ts")
        let mine = ScreenSharePolicy.controlChallenge(sessionId: "s1", timestamp: "ts")
        let result = await host.requestSession(
            sessionId: "s1", deviceId: "p1", requestedMode: .control,
            controlChallenge: mine, controlSignature: keystore.sign(challenge: other),
            surface: quietSurface())
        #expect(isFailure(result, .controlSignatureInvalid))
    }

    @Test func controlWithNoEnrolledKeyIsRefused() async {
        let keystore = FakeKeystore()
        let host = ScreenShareHost()
        // Grant without an enrolled key: control has never been set up.
        await host.setDeviceSettings(ScreenShareDeviceSettings(deviceId: "p1", allowed: true, grant: .control))
        let challenge = ScreenSharePolicy.controlChallenge(sessionId: "s1", timestamp: "ts")
        let result = await host.requestSession(
            sessionId: "s1", deviceId: "p1", requestedMode: .control,
            controlChallenge: challenge, controlSignature: keystore.sign(challenge: challenge),
            surface: quietSurface())
        #expect(isFailure(result, .controlSignatureInvalid))
    }

    @Test func controlWithValidSignatureIsAdmittedAndEnablesInjection() async {
        let peer = ScriptedFakePeer()
        let host = await hostWithControlSession(surface: peer.surface())
        #expect(await host.sessionCount == 1)
        #expect(await host.isInjectionEnabled)
        #expect(await host.canInject())
    }

    @Test func challengeIsBoundToTheSession() {
        let a = ScreenSharePolicy.controlChallenge(sessionId: "s1", timestamp: "t")
        let b = ScreenSharePolicy.controlChallenge(sessionId: "s2", timestamp: "t")
        let c = ScreenSharePolicy.controlChallenge(sessionId: "s1", timestamp: "u")
        #expect(a != b && a != c)
        let keystore = FakeKeystore()
        #expect(ScreenSharePolicy.verifyControlSignature(
            challenge: a, signature: keystore.sign(challenge: a), publicKeyData: keystore.publicKeyData))
        #expect(!ScreenSharePolicy.verifyControlSignature(
            challenge: b, signature: keystore.sign(challenge: a), publicKeyData: keystore.publicKeyData))
        #expect(!ScreenSharePolicy.verifyControlSignature(
            challenge: a, signature: Data([0x01, 0x02]), publicKeyData: keystore.publicKeyData))
        #expect(!ScreenSharePolicy.verifyControlSignature(
            challenge: a, signature: keystore.sign(challenge: a), publicKeyData: Data([0x04])))
    }

    // MARK: - 4. Kill triggers: capture, injection and peer stop inside the deadline

    @Test func killSwitchStopsEverythingWithTheRelayDown() async {
        let peer = ScriptedFakePeer()
        peer.relayUp = false
        let host = await hostWithControlSession(surface: peer.surface())
        #expect(await host.canInject())

        let timing = await host.killAll(reason: .killSwitch)

        #expect(timing.elapsed <= ScreenSharePolicy.killDeadline)
        #expect(timing.deadlineExceeded == false)
        #expect(timing.reason == .killSwitch)
        #expect(await host.canInject() == false)
        #expect(await host.isCaptureActive == false)
        #expect(await host.sessionCount == 0)
        // Teardown finished locally although every relay send failed.
        #expect(peer.events.contains("relay-send-failed"))
        #expect(peer.events.contains("peer-closed"))
        #expect(peer.events.contains("capture-stopped"))
    }

    @Test func revokeDowngradeAndRekeyStopEverythingWithTheRelayDown() async {
        for reason in [ScreenShareStopReason.revoked, .grantDowngrade, .rekeyPairing] {
            let peer = ScriptedFakePeer()
            peer.relayUp = false
            let host = await hostWithControlSession(surface: peer.surface())
            #expect(await host.canInject())

            let timing = await host.killDevice(deviceId: "p1", reason: reason)

            #expect(timing.elapsed <= ScreenSharePolicy.killDeadline)
            #expect(timing.reason == reason)
            #expect(await host.canInject() == false)
            #expect(await host.isCaptureActive == false)
            #expect(await host.sessionCount == 0)
            #expect(peer.events.contains("relay-send-failed"))
            #expect(peer.events.contains("peer-closed"))
            #expect(peer.events.contains("capture-stopped"))
        }
    }

    /// The host's promise is a deadline, not a hope: a peer whose close never
    /// returns is abandoned at 1 s, and everything the host controls is already
    /// off before the first surface is touched.
    @Test func aHungPeerCloseIsCutOffAtTheDeadline() async {
        let peer = ScriptedFakePeer()
        peer.relayUp = false
        peer.closeHangs = true
        let host = await hostWithControlSession(surface: peer.surface())
        #expect(await host.canInject())

        let wall = Date()
        let timing = await host.killAll(reason: .killSwitch)
        let waited = Date().timeIntervalSince(wall)

        #expect(timing.deadlineExceeded)
        // Cut off at the deadline rather than waiting on the 60 s hang.
        #expect(waited < ScreenSharePolicy.killDeadline + 1)
        #expect(timing.elapsed < ScreenSharePolicy.killDeadline + 1)
        #expect(await host.canInject() == false)
        #expect(await host.isCaptureActive == false)
        #expect(await host.sessionCount == 0)
        #expect(peer.events.contains("peer-close-hung"))
        #expect(!peer.events.contains("peer-close-returned-late"))
        #expect(await waitUntil { peer.events.contains("peer-close-cut-off") })
    }

    /// Everything the host answers for is already false at t0, so nothing can
    /// slip through while the surfaces are still being torn down.
    @Test func noInjectionIsAdmittedDuringTeardownAfterT0() async {
        let peer = ScriptedFakePeer()
        let host = ScreenShareHost()
        peer.injectionProbe = { @Sendable in await host.canInject() }
        _ = await hostWithControlSession(host: host, surface: peer.surface())
        #expect(await host.canInject())

        await host.killAll(reason: .killSwitch)

        #expect(peer.events.contains("peer-closed"))
        #expect(peer.injectionsAfterT0 == 0)
        #expect(await host.canInject() == false)
    }

    @Test func killingOneDeviceLeavesAnotherPhonesViewSessionRunning() async {
        let controller = ScriptedFakePeer(), viewer = ScriptedFakePeer()
        let host = ScreenShareHost()
        await host.setDeviceSettings(ScreenShareDeviceSettings(deviceId: "v1", allowed: true, grant: .view))
        _ = await hostWithControlSession(host: host, deviceId: "p1", sessionId: "s1", surface: controller.surface())
        _ = await host.requestSession(
            sessionId: "s2", deviceId: "v1", requestedMode: .view,
            controlChallenge: nil, controlSignature: nil, surface: viewer.surface())
        #expect(await host.sessionCount == 2)

        await host.killDevice(deviceId: "p1", reason: .revoked)

        #expect(await host.sessionCount == 1)
        // The viewer keeps its frames; the controller's injection is gone.
        #expect(await host.isCaptureActive)
        #expect(await host.canInject() == false)
        #expect(controller.events.contains("peer-closed"))
        #expect(!viewer.events.contains("peer-closed"))
    }

    // MARK: - 5. Real idle timers: 10 min in control, 30 min in view-only

    @Test func viewOnlySessionEndsAfterThirtyIdleMinutes() async {
        let clock = ScreenShareTestClock(Date(timeIntervalSinceReferenceDate: 0))
        let waits = ScreenShareIntervalLog()
        let peer = ScriptedFakePeer()
        let host = ScreenShareHost(now: { clock.date }, idleSleep: { seconds in
            waits.add(seconds); clock.advance(by: seconds); await Task.yield()
        })
        await host.setDeviceSettings(ScreenShareDeviceSettings(deviceId: "p1", allowed: true, grant: .view))
        _ = await host.requestSession(
            sessionId: "s1", deviceId: "p1", requestedMode: .view,
            controlChallenge: nil, controlSignature: nil, surface: peer.surface())
        #expect(await host.sessionCount == 1)

        // The teardown the timer starts is awaited, so the timing lands a moment
        // after the session is gone.
        #expect(await waitUntil { await host.latestKillTiming() != nil })
        // The real timer waited the view-only timeout, not the control one.
        #expect(waits.values == [ScreenSharePolicy.viewIdleTimeout])
        #expect(await host.sessionCount == 0)
        #expect(await host.isCaptureActive == false)
        #expect(peer.events.contains("peer-closed"))
        let timing = await host.latestKillTiming()
        #expect(timing?.reason == .idleTimeout)
        #expect(timing?.deadlineExceeded == false)
        #expect(await host.log().first?.endedAt != nil)
    }

    @Test func controlSessionEndsAfterTenIdleMinutes() async {
        let clock = ScreenShareTestClock(Date(timeIntervalSinceReferenceDate: 0))
        let waits = ScreenShareIntervalLog()
        let peer = ScriptedFakePeer()
        let host = ScreenShareHost(now: { clock.date }, idleSleep: { seconds in
            waits.add(seconds); clock.advance(by: seconds); await Task.yield()
        })
        _ = await hostWithControlSession(host: host, surface: peer.surface())
        #expect(await host.canInject())

        #expect(await waitUntil { await host.latestKillTiming() != nil })
        #expect(waits.values == [ScreenSharePolicy.controlIdleTimeout])
        #expect(await host.sessionCount == 0)
        #expect(await host.canInject() == false)
        #expect(await host.latestKillTiming()?.reason == .idleTimeout)
    }

    /// Activity that lands before the timer fires pushes the deadline out
    /// instead of ending the session: the timer re-arms for the remainder.
    @Test func activityRearmsTheIdleTimerInsteadOfEndingTheSession() async {
        let clock = ScreenShareTestClock(Date(timeIntervalSinceReferenceDate: 0))
        let waits = ScreenShareIntervalLog()
        let holder = ScreenShareHostBox()
        let host = ScreenShareHost(now: { clock.date }, idleSleep: { seconds in
            waits.add(seconds)
            clock.advance(by: seconds)
            // The phone moved the pointer just as the first timer expired.
            if waits.values.count == 1, let host = holder.host {
                await host.noteActivity(sessionId: "s1")
            }
            await Task.yield()
        })
        holder.host = host
        _ = await hostWithControlSession(host: host, surface: quietSurface())

        #expect(await waitUntil { await host.sessionCount == 0 })
        // Two full control timeouts: the first was reset by the activity.
        #expect(waits.values == [ScreenSharePolicy.controlIdleTimeout, ScreenSharePolicy.controlIdleTimeout])
    }

    @Test func idleTimeoutValuesMatchTheSpec() {
        #expect(ScreenSharePolicy.idleTimeout(for: .control) == 600)
        #expect(ScreenSharePolicy.idleTimeout(for: .view) == 1800)
        #expect(ScreenSharePolicy.controlIdleTimeout == 600)
        #expect(ScreenSharePolicy.viewIdleTimeout == 1800)
        #expect(ScreenSharePolicy.backgroundTimeout == 30)
    }

    // MARK: - 6. Concurrency: one controller plus two viewers

    @Test func atMostOneControllerIsAdmitted() async {
        let first = FakeKeystore(), second = FakeKeystore()
        let host = ScreenShareHost()
        await host.setDeviceSettings(ScreenShareDeviceSettings(
            deviceId: "p1", allowed: true, grant: .control, controlKeyPublicData: first.publicKeyData))
        await host.setDeviceSettings(ScreenShareDeviceSettings(
            deviceId: "p2", allowed: true, grant: .control, controlKeyPublicData: second.publicKeyData))
        let challengeOne = ScreenSharePolicy.controlChallenge(sessionId: "c1", timestamp: "t1")
        #expect(isSuccess(await host.requestSession(
            sessionId: "c1", deviceId: "p1", requestedMode: .control,
            controlChallenge: challengeOne, controlSignature: first.sign(challenge: challengeOne),
            surface: quietSurface())))
        let challengeTwo = ScreenSharePolicy.controlChallenge(sessionId: "c2", timestamp: "t2")
        #expect(isFailure(await host.requestSession(
            sessionId: "c2", deviceId: "p2", requestedMode: .control,
            controlChallenge: challengeTwo, controlSignature: second.sign(challenge: challengeTwo),
            surface: quietSurface()), .concurrencyLimit))
    }

    @Test func atMostTwoViewersAreAdmittedAlongsideTheController() async {
        let host = ScreenShareHost()
        for id in ["v1", "v2", "v3"] {
            await host.setDeviceSettings(ScreenShareDeviceSettings(deviceId: id, allowed: true, grant: .view))
        }
        _ = await hostWithControlSession(host: host, deviceId: "p1", sessionId: "c1", surface: quietSurface())
        for (index, id) in ["v1", "v2"].enumerated() {
            #expect(isSuccess(await host.requestSession(
                sessionId: "v-\(index)", deviceId: id, requestedMode: .view,
                controlChallenge: nil, controlSignature: nil, surface: quietSurface())))
        }
        #expect(isFailure(await host.requestSession(
            sessionId: "v-2", deviceId: "v3", requestedMode: .view,
            controlChallenge: nil, controlSignature: nil, surface: quietSurface()), .concurrencyLimit))
        // One controller plus two viewers, and nothing more.
        #expect(await host.sessionCount == 3)
        #expect(await host.liveSessions().filter { $0.mode == .control }.count == 1)
        #expect(await host.liveSessions().filter { $0.mode == .view }.count == 2)
    }

    @Test func concurrencyRulesAndConstants() {
        #expect(ScreenSharePolicy.maxControllers == 1)
        #expect(ScreenSharePolicy.maxViewers == 2)
        #expect(ScreenSharePolicy.canJoin(mode: .control, counts: .init(controllers: 0, viewers: 2)))
        #expect(!ScreenSharePolicy.canJoin(mode: .control, counts: .init(controllers: 1, viewers: 0)))
        #expect(ScreenSharePolicy.canJoin(mode: .view, counts: .init(controllers: 1, viewers: 1)))
        #expect(!ScreenSharePolicy.canJoin(mode: .view, counts: .init(controllers: 0, viewers: 2)))
    }

    // MARK: - 7. Local HID activity pauses remote input for 2 s

    @Test func localHidPausesInjectionForTwoSeconds() async {
        let clock = ScreenShareTestClock(Date(timeIntervalSinceReferenceDate: 0))
        let host = ScreenShareHost(now: { clock.date })
        _ = await hostWithControlSession(host: host, surface: quietSurface())
        #expect(await host.canInject())

        await host.notifyLocalHID()
        #expect(await host.canInject() == false)
        clock.advance(by: 1.5)
        #expect(await host.canInject() == false)
        clock.advance(by: 0.7)
        #expect(await host.canInject())
        #expect(ScreenSharePolicy.hidPauseDuration == 2)
    }

    // MARK: - 8. Lock screen and secure input stop frames and reject injection

    @Test func lockScreenStopsFramesAndRejectsInjection() async {
        let peer = ScriptedFakePeer()
        let host = await hostWithControlSession(surface: peer.surface())
        #expect(await host.canInject())
        #expect(await host.isCaptureActive)

        await host.setLocked(true)
        #expect(await host.canInject() == false)
        #expect(await host.isCaptureActive == false)
        #expect(peer.events.contains("capture-stopped"))
        // The session itself survives, so the phone can show the state.
        #expect(await host.sessionCount == 1)

        await host.setLocked(false)
        #expect(await host.isCaptureActive)
        #expect(await host.canInject())
    }

    @Test func secureInputStopsFramesAndRejectsInjection() async {
        let peer = ScriptedFakePeer()
        let host = await hostWithControlSession(surface: peer.surface())
        await host.setSecureInput(true)
        #expect(await host.canInject() == false)
        #expect(await host.isCaptureActive == false)
        #expect(await host.isSecureInputActive)

        await host.setSecureInput(false)
        #expect(await host.isCaptureActive)
        #expect(await host.canInject())
    }

    @Test func framesStayStoppedWhileEitherStateHolds() async {
        let peer = ScriptedFakePeer()
        let host = await hostWithControlSession(surface: peer.surface())
        await host.setLocked(true)
        await host.setSecureInput(true)
        await host.setLocked(false)
        // Secure input is still on, so frames must not come back.
        #expect(await host.isCaptureActive == false)
        #expect(await host.canInject() == false)
        await host.setSecureInput(false)
        #expect(await host.isCaptureActive)
    }

    @Test func aSessionAdmittedWhileLockedStartsWithoutFrames() async {
        let peer = ScriptedFakePeer()
        let host = ScreenShareHost()
        await host.setLocked(true)
        await host.setDeviceSettings(ScreenShareDeviceSettings(deviceId: "p1", allowed: true, grant: .view))
        #expect(isSuccess(await host.requestSession(
            sessionId: "s1", deviceId: "p1", requestedMode: .view,
            controlChallenge: nil, controlSignature: nil, surface: peer.surface())))
        #expect(await host.isCaptureActive == false)
        #expect(!peer.events.contains("capture-started"))
        await host.setLocked(false)
        #expect(await host.isCaptureActive)
        #expect(peer.events.contains("capture-started"))
    }

    @Test func injectionBlockedPolicyFunction() {
        #expect(ScreenSharePolicy.injectionBlocked(locked: true, secureInputActive: false))
        #expect(ScreenSharePolicy.injectionBlocked(locked: false, secureInputActive: true))
        #expect(ScreenSharePolicy.injectionBlocked(locked: true, secureInputActive: true))
        #expect(!ScreenSharePolicy.injectionBlocked(locked: false, secureInputActive: false))
    }

    // MARK: - 9. The session log holds no keystrokes

    @Test func sessionLogRecordsDeviceTimeAndModeOnly() async {
        let host = ScreenShareHost()
        await host.setDeviceSettings(ScreenShareDeviceSettings(deviceId: "p1", allowed: true, grant: .view))
        _ = await host.requestSession(
            sessionId: "s1", deviceId: "p1", requestedMode: .view,
            controlChallenge: nil, controlSignature: nil, surface: quietSurface())
        await host.killAll(reason: .killSwitch)

        let entries = await host.log()
        #expect(entries.count == 1)
        let entry = entries[0]
        #expect(entry.deviceId == "p1")
        #expect(entry.mode == .view)
        #expect(entry.endedAt != nil)
        // The type carries no keystroke field at all — checked by reflection so
        // adding one later breaks this test rather than leaking quietly.
        let properties = Set(Mirror(reflecting: entry).children.compactMap(\.label))
        #expect(properties == ["sessionId", "deviceId", "mode", "startedAt", "endedAt"])
    }

    // MARK: - 10. Normalized coordinates map to CGDisplayBounds

    @Test func normalizedCoordinatesMapIntoTheDisplayBounds() {
        let bounds = CGRect(x: 100, y: 200, width: 1920, height: 1080)
        let topLeft = ScreenSharePolicy.map(point: .init(displayId: 1, x: 0, y: 0), bounds: bounds)
        #expect(topLeft == CGPoint(x: 100, y: 200))
        let bottomRight = ScreenSharePolicy.map(point: .init(displayId: 1, x: 1, y: 1), bounds: bounds)
        #expect(bottomRight == CGPoint(x: 2020, y: 1280))
        let centre = ScreenSharePolicy.map(point: .init(displayId: 1, x: 0.5, y: 0.5), bounds: bounds)
        #expect(centre == CGPoint(x: 1060, y: 740))
        // Out-of-range values are clamped into the display.
        let overshot = ScreenSharePolicy.map(point: .init(displayId: 1, x: 1.5, y: -0.1), bounds: bounds)
        #expect(overshot == CGPoint(x: 2020, y: 200))
    }

    @Test func displayIdTravelsWithThePoint() {
        let point = ScreenShareNormalizedPoint(displayId: 42, x: 0.25, y: 0.75)
        #expect(point.displayId == 42)
        #expect(ScreenSharePolicy.map(point: point, bounds: CGRect(x: 0, y: 0, width: 400, height: 400))
            == CGPoint(x: 100, y: 300))
    }

    // MARK: - Kill while capture is starting (actor re-entrancy)

    /// A capture start the test releases by hand, so a kill can land while the
    /// host is suspended inside `startCapture`.
    final class HeldCaptureStart: @unchecked Sendable {
        private let lock = NSLock()
        private var log: [String] = []
        private var waiter: CheckedContinuation<Void, Never>?
        private var released = false
        var events: [String] { lock.lock(); defer { lock.unlock() }; return log }
        private func record(_ event: String) { lock.lock(); log.append(event); lock.unlock() }

        func release() {
            lock.lock(); released = true; let w = waiter; waiter = nil; lock.unlock()
            w?.resume()
        }

        func surface() -> ScreenShareSurface {
            ScreenShareSurface(
                startCapture: { @Sendable in
                    self.record("capture-start-begun")
                    await withCheckedContinuation { (c: CheckedContinuation<Void, Never>) in
                        self.lock.lock()
                        if self.released { self.lock.unlock(); c.resume(); return }
                        self.waiter = c
                        self.lock.unlock()
                    }
                    self.record("capture-started")
                },
                stopCapture: { @Sendable in self.record("capture-stopped") },
                closePeer: { @Sendable in self.record("peer-closed") })
        }
    }

    @Test func aKillDuringStartCaptureRefusesTheJoinAndStopsTheLateCapture() async {
        let held = HeldCaptureStart()
        let host = ScreenShareHost()
        let keystore = FakeKeystore()
        await host.setDeviceSettings(ScreenShareDeviceSettings(
            deviceId: "p1", allowed: true, grant: .control,
            controlKeyPublicData: keystore.publicKeyData))
        let challenge = ScreenSharePolicy.controlChallenge(sessionId: "s1", timestamp: "ts")
        let join = Task {
            await host.requestSession(
                sessionId: "s1", deviceId: "p1", requestedMode: .control,
                controlChallenge: challenge, controlSignature: keystore.sign(challenge: challenge),
                surface: held.surface())
        }
        #expect(await waitUntil { held.events.contains("capture-start-begun") })

        // The kill switch lands while the host is suspended inside startCapture.
        let timing = await host.killAll(reason: .killSwitch)
        #expect(timing.elapsed <= ScreenSharePolicy.killDeadline)
        #expect(await host.sessionCount == 0)

        // Capture finishes starting only after the kill.
        held.release()
        let result = await join.value

        #expect(isFailure(result, .sessionStopped))
        #expect(await host.isInjectionEnabled == false)
        #expect(await host.canInject() == false)
        #expect(await host.isCaptureActive == false)
        #expect(await host.sessionCount == 0)
        // The capture that came up after the kill was stopped again.
        let events = held.events
        let started = events.firstIndex(of: "capture-started")
        #expect(started != nil)
        #expect(events.last == "capture-stopped")
        if let started { #expect(events[started...].contains("capture-stopped")) }
    }

    @Test func aKillWhileCaptureResumesAfterUnlockStopsTheLateCapture() async {
        let held = HeldCaptureStart()
        let host = ScreenShareHost()
        // Admitted while locked: no capture yet, so the first start comes from
        // the unlock path.
        await host.setLocked(true)
        await host.setDeviceSettings(ScreenShareDeviceSettings(deviceId: "p1", allowed: true, grant: .view))
        #expect(isSuccess(await host.requestSession(
            sessionId: "s1", deviceId: "p1", requestedMode: .view,
            controlChallenge: nil, controlSignature: nil, surface: held.surface())))

        let unlock = Task { await host.setLocked(false) }
        #expect(await waitUntil { held.events.contains("capture-start-begun") })
        _ = await host.killAll(reason: .killSwitch)
        held.release()
        await unlock.value

        #expect(await host.isCaptureActive == false)
        #expect(held.events.last == "capture-stopped")
    }

    @Test func killDeadlineIsOneSecond() {
        #expect(ScreenSharePolicy.killDeadline == 1)
    }

    // MARK: - Helpers

    /// Waits for an actor-held condition without pinning a wall-clock duration
    /// into the assertion.
    private func waitUntil(timeout: TimeInterval = 5, _ condition: @Sendable () async -> Bool) async -> Bool {
        let deadline = Date().addingTimeInterval(timeout)
        while Date() < deadline {
            if await condition() { return true }
            try? await Task.sleep(nanoseconds: 2_000_000)
        }
        return await condition()
    }
}

// MARK: - Test doubles

/// A clock the test advances by hand.
final class ScreenShareTestClock: @unchecked Sendable {
    private let lock = NSLock()
    private var stamp: Date
    init(_ date: Date) { stamp = date }
    var date: Date { lock.lock(); defer { lock.unlock() }; return stamp }
    func advance(by seconds: TimeInterval) {
        lock.lock(); stamp = stamp.addingTimeInterval(seconds); lock.unlock()
    }
}

/// Every interval the host's idle timer actually asked to wait.
final class ScreenShareIntervalLog: @unchecked Sendable {
    private let lock = NSLock()
    private var intervals: [TimeInterval] = []
    var values: [TimeInterval] { lock.lock(); defer { lock.unlock() }; return intervals }
    func add(_ value: TimeInterval) { lock.lock(); intervals.append(value); lock.unlock() }
}

/// Lets a closure passed into the host's initialiser reach the host afterwards.
final class ScreenShareHostBox: @unchecked Sendable {
    private let lock = NSLock()
    private var value: ScreenShareHost?
    var host: ScreenShareHost? {
        get { lock.lock(); defer { lock.unlock() }; return value }
        set { lock.lock(); value = newValue; lock.unlock() }
    }
}
