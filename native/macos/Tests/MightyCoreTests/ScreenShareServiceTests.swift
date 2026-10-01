import CoreGraphics
import Foundation
import Testing
@testable import MightyCore

/// Proves the production screen-share path — the allow-list on disk, the
/// settings actions, the relay's join requests, the input route and the
/// menu-bar kill switch — enforces every rule on the Mac, with a scripted fake
/// peer and no display, relay or PeerConnection in sight.
struct ScreenShareServiceTests {
    typealias Peer = ScreenShareSafetyTests.ScriptedFakePeer
    typealias Keystore = ScreenShareSafetyTests.FakeKeystore

    private static let phone = "phone-one-00000001"
    private static let viewer = "phone-two-00000002"

    // MARK: Fixture

    private struct Fixture {
        let directory: URL
        let store: ScreenShareSettingsStore
        let host: ScreenShareHost
        let service: ScreenShareService
        let displays: FakeDisplays
        let input: FakeInput
        let environment: FakeEnvironment
        let clock: ScreenShareTestClock
    }

    private func makeFixture(
        layout: [UInt32: CGRect] = [1: CGRect(x: 0, y: 0, width: 1920, height: 1080),
                                    2: CGRect(x: 1920, y: 0, width: 1280, height: 800)],
        main: UInt32 = 1
    ) -> Fixture {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("screen-share-" + UUID().uuidString, isDirectory: true)
        let store = ScreenShareSettingsStore(url: directory.appendingPathComponent("screen-share.json"))
        let clock = ScreenShareTestClock(Date(timeIntervalSinceReferenceDate: 0))
        let host = ScreenShareHost(now: { clock.date })
        let displays = FakeDisplays(layout: layout, main: main)
        let input = FakeInput()
        let environment = FakeEnvironment()
        let service = ScreenShareService(store: store, host: host, displays: displays,
                                        input: input, environment: environment)
        return Fixture(directory: directory, store: store, host: host, service: service,
                       displays: displays, input: input, environment: environment, clock: clock)
    }

    private func cleanUp(_ fixture: Fixture) {
        try? FileManager.default.removeItem(at: fixture.directory)
    }

    /// Grants control to `phone`, enrols a key and joins a signed session.
    private func joinControl(
        _ fixture: Fixture, deviceId: String = phone, sessionId: String = "s1", peer: Peer
    ) async -> (Keystore, Result<Void, ScreenShareError>) {
        let keystore = Keystore()
        try? await fixture.service.setAllowed(deviceId: deviceId, allowed: true)
        try? await fixture.service.setGrant(deviceId: deviceId, grant: .control,
                                           controlKeyPublicData: keystore.publicKeyData)
        let challenge = await fixture.service.controlChallenge(sessionId: sessionId)
        let result = await fixture.service.join(
            sessionId: sessionId, deviceId: deviceId, mode: .control,
            controlChallenge: challenge, controlSignature: keystore.sign(challenge: challenge),
            surface: peer.surface())
        return (keystore, result)
    }

    private func isFailure(_ result: Result<Void, ScreenShareError>, _ expected: ScreenShareError) -> Bool {
        if case .failure(let error) = result { return error == expected }; return false
    }
    private func isSuccess(_ result: Result<Void, ScreenShareError>) -> Bool {
        if case .success = result { return true }; return false
    }

    // MARK: - Allow-list is off by default

    @Test func aPairedPhoneIsRefusedUntilTheUserEnablesIt() async {
        let fixture = makeFixture(); defer { cleanUp(fixture) }
        // Nothing on disk: pairing a phone has granted it nothing.
        #expect(await fixture.service.allowList().isEmpty)
        let refused = await fixture.service.join(
            sessionId: "s1", deviceId: Self.phone, mode: .view,
            controlChallenge: nil, controlSignature: nil, surface: ScreenShareSurface(
                startCapture: {}, stopCapture: {}, closePeer: {}))
        #expect(isFailure(refused, .deviceNotAllowed))
        #expect(await fixture.service.liveSessions().isEmpty)
    }

    @Test func enablingAPhoneStillGrantsNothingUntilAGrantIsGiven() async throws {
        let fixture = makeFixture(); defer { cleanUp(fixture) }
        try await fixture.service.setAllowed(deviceId: Self.phone, allowed: true)
        let row = await fixture.service.settings(for: Self.phone)
        #expect(row?.allowed == true && row?.grant == ScreenShareGrant.none)
        #expect(isFailure(await fixture.service.join(
            sessionId: "s1", deviceId: Self.phone, mode: .view,
            controlChallenge: nil, controlSignature: nil,
            surface: Peer().surface()), .insufficientGrant))
    }

    @Test func aPhoneWithoutAClientIdIsRefused() async throws {
        let fixture = makeFixture(); defer { cleanUp(fixture) }
        // The legacy row is shared by every phone that predates client ids, so
        // it can never be told apart — and never be allow-listed.
        try await fixture.service.setAllowed(deviceId: MobileDeviceRegistry.legacyId, allowed: true)
        try await fixture.service.setGrant(deviceId: MobileDeviceRegistry.legacyId, grant: .view)
        #expect(isFailure(await fixture.service.join(
            sessionId: "s1", deviceId: MobileDeviceRegistry.legacyId, mode: .view,
            controlChallenge: nil, controlSignature: nil,
            surface: Peer().surface()), .deviceNotAllowed))
    }

    @Test func viewAndControlAreSeparateGrants() async throws {
        let fixture = makeFixture(); defer { cleanUp(fixture) }
        let keystore = Keystore()
        try await fixture.service.setAllowed(deviceId: Self.phone, allowed: true)
        try await fixture.service.setGrant(deviceId: Self.phone, grant: .view)
        // A view grant admits a view session and refuses control.
        #expect(isSuccess(await fixture.service.join(
            sessionId: "s1", deviceId: Self.phone, mode: .view,
            controlChallenge: nil, controlSignature: nil, surface: Peer().surface())))
        let challenge = await fixture.service.controlChallenge(sessionId: "s2")
        #expect(isFailure(await fixture.service.join(
            sessionId: "s2", deviceId: Self.phone, mode: .control,
            controlChallenge: challenge, controlSignature: keystore.sign(challenge: challenge),
            surface: Peer().surface()), .insufficientGrant))
        // Granting view never keeps a control key around.
        #expect(await fixture.service.settings(for: Self.phone)?.controlKeyPublicData == nil)
    }

    @Test func controlStartNeedsTheEnrolledKeysSignature() async throws {
        let fixture = makeFixture(); defer { cleanUp(fixture) }
        let enrolled = Keystore(), other = Keystore()
        try await fixture.service.setAllowed(deviceId: Self.phone, allowed: true)
        try await fixture.service.setGrant(deviceId: Self.phone, grant: .control,
                                          controlKeyPublicData: enrolled.publicKeyData)
        // Another phone's key, and no signature at all, are both refused — and
        // each attempt spends its challenge, so every try asks for a new one.
        var challenge = await fixture.service.controlChallenge(sessionId: "s1")
        #expect(isFailure(await fixture.service.join(
            sessionId: "s1", deviceId: Self.phone, mode: .control,
            controlChallenge: challenge, controlSignature: other.sign(challenge: challenge),
            surface: Peer().surface()), .controlSignatureInvalid))
        challenge = await fixture.service.controlChallenge(sessionId: "s1")
        #expect(isFailure(await fixture.service.join(
            sessionId: "s1", deviceId: Self.phone, mode: .control,
            controlChallenge: challenge, controlSignature: nil,
            surface: Peer().surface()), .controlSignatureInvalid))
        challenge = await fixture.service.controlChallenge(sessionId: "s1")
        let signature = enrolled.sign(challenge: challenge)
        #expect(isSuccess(await fixture.service.join(
            sessionId: "s1", deviceId: Self.phone, mode: .control,
            controlChallenge: challenge, controlSignature: signature,
            surface: Peer().surface())))
    }

    @Test func aUsedControlSignatureCannotStartAnotherSession() async throws {
        let fixture = makeFixture(); defer { cleanUp(fixture) }
        let enrolled = Keystore()
        try await fixture.service.setAllowed(deviceId: Self.phone, allowed: true)
        try await fixture.service.setGrant(deviceId: Self.phone, grant: .control,
                                          controlKeyPublicData: enrolled.publicKeyData)
        let challenge = await fixture.service.controlChallenge(sessionId: "s1")
        let signature = enrolled.sign(challenge: challenge)
        #expect(isSuccess(await fixture.service.join(
            sessionId: "s1", deviceId: Self.phone, mode: .control,
            controlChallenge: challenge, controlSignature: signature, surface: Peer().surface())))
        await fixture.service.killSwitch()

        // Replaying the same challenge and signature starts nothing: the phone
        // has to pass its biometric gate again for every control session.
        #expect(isFailure(await fixture.service.join(
            sessionId: "s1", deviceId: Self.phone, mode: .control,
            controlChallenge: challenge, controlSignature: signature,
            surface: Peer().surface()), .controlSignatureInvalid))
        // A challenge minted for another session is refused as well.
        let other = await fixture.service.controlChallenge(sessionId: "s2")
        #expect(isFailure(await fixture.service.join(
            sessionId: "s3", deviceId: Self.phone, mode: .control,
            controlChallenge: other, controlSignature: enrolled.sign(challenge: other),
            surface: Peer().surface()), .controlSignatureInvalid))
        #expect(await fixture.service.liveSessions().isEmpty)
    }

    @Test func theAllowListSurvivesARestartAndStaysOwnerOnly() async throws {
        let fixture = makeFixture(); defer { cleanUp(fixture) }
        let keystore = Keystore()
        try await fixture.service.setAllowed(deviceId: Self.phone, allowed: true)
        try await fixture.service.setGrant(deviceId: Self.phone, grant: .control,
                                          controlKeyPublicData: keystore.publicKeyData)
        let url = fixture.directory.appendingPathComponent("screen-share.json")
        #expect((try FileManager.default.attributesOfItem(atPath: url.path)[.posixPermissions] as? Int) == 0o600)
        let reopened = ScreenShareSettingsStore(url: url)
        let row = reopened.settings(for: Self.phone)
        #expect(row?.allowed == true && row?.grant == .control)
        #expect(row?.controlKeyPublicData == keystore.publicKeyData)
        // A phone nobody enabled is still absent, not merely disabled.
        #expect(reopened.settings(for: Self.viewer) == nil)
    }

    // MARK: - Kill triggers

    @Test func withdrawingTheControlGrantStopsTheSessionInsideTheDeadline() async throws {
        let fixture = makeFixture(); defer { cleanUp(fixture) }
        let peer = Peer()
        peer.relayUp = false
        _ = await joinControl(fixture, peer: peer)
        #expect(await fixture.host.canInject())

        try await fixture.service.setGrant(deviceId: Self.phone, grant: .view)

        #expect(await fixture.service.liveSessions().isEmpty)
        #expect(await fixture.host.canInject() == false)
        #expect(await fixture.host.isCaptureActive == false)
        #expect(peer.events.contains("peer-closed"))
        let timing = await fixture.host.latestKillTiming()
        #expect(timing?.reason == .grantDowngrade)
        #expect((timing?.elapsed ?? 99) <= ScreenSharePolicy.killDeadline)
        // The enrolled biometric key goes with the grant.
        #expect(await fixture.service.settings(for: Self.phone)?.controlKeyPublicData == nil)
    }

    @Test func takingThePhoneOffTheAllowListStopsItsSession() async throws {
        let fixture = makeFixture(); defer { cleanUp(fixture) }
        let peer = Peer()
        _ = await joinControl(fixture, peer: peer)

        try await fixture.service.setAllowed(deviceId: Self.phone, allowed: false)

        #expect(await fixture.service.liveSessions().isEmpty)
        #expect(await fixture.host.canInject() == false)
        #expect(await fixture.service.settings(for: Self.phone)?.grant == ScreenShareGrant.none)
        #expect(await fixture.host.latestKillTiming()?.reason == .revoked)
    }

    @Test func revokingADeviceClearsItsRowAndStopsItsSession() async {
        let fixture = makeFixture(); defer { cleanUp(fixture) }
        let peer = Peer()
        peer.relayUp = false
        _ = await joinControl(fixture, peer: peer)

        await fixture.service.deviceRevoked(Self.phone)

        #expect(await fixture.service.settings(for: Self.phone) == nil)
        #expect(await fixture.service.liveSessions().isEmpty)
        #expect(await fixture.host.isCaptureActive == false)
        #expect(peer.events.contains("peer-closed"))
        #expect(await fixture.host.latestKillTiming()?.reason == .revoked)
    }

    @Test func regeneratingThePairingKeyClearsEveryRowAndStopsEverySession() async throws {
        let fixture = makeFixture(); defer { cleanUp(fixture) }
        let controller = Peer(), watcher = Peer()
        // The relay is down: a rekey must still tear everything down locally.
        controller.relayUp = false
        watcher.relayUp = false
        _ = await joinControl(fixture, peer: controller)
        try await fixture.service.setAllowed(deviceId: Self.viewer, allowed: true)
        try await fixture.service.setGrant(deviceId: Self.viewer, grant: .view)
        _ = await fixture.service.join(
            sessionId: "s2", deviceId: Self.viewer, mode: .view,
            controlChallenge: nil, controlSignature: nil, surface: watcher.surface())
        #expect(await fixture.service.liveSessions().count == 2)

        await fixture.service.pairingKeyRegenerated()

        #expect(await fixture.service.allowList().isEmpty)
        #expect(await fixture.service.liveSessions().isEmpty)
        #expect(await fixture.host.canInject() == false)
        #expect(await fixture.host.isCaptureActive == false)
        #expect(controller.events.contains("peer-closed"))
        #expect(watcher.events.contains("peer-closed"))
        #expect(controller.events.contains("relay-send-failed"))
        #expect(await fixture.host.latestKillTiming()?.reason == .rekeyPairing)
        // Every phone starts disabled again after a rekey.
        #expect(isFailure(await fixture.service.join(
            sessionId: "s3", deviceId: Self.phone, mode: .view,
            controlChallenge: nil, controlSignature: nil,
            surface: Peer().surface()), .deviceNotAllowed))
    }

    @Test func theMenuBarKillSwitchStopsEverythingEvenWithAHungPeer() async {
        let fixture = makeFixture(); defer { cleanUp(fixture) }
        let peer = Peer()
        peer.relayUp = false
        peer.closeHangs = true
        _ = await joinControl(fixture, peer: peer)
        #expect(await fixture.host.canInject())

        let wall = Date()
        let timing = await fixture.service.killSwitch()
        let waited = Date().timeIntervalSince(wall)

        #expect(timing.reason == .killSwitch)
        #expect(timing.deadlineExceeded)
        #expect(waited < ScreenSharePolicy.killDeadline + 1)
        #expect(await fixture.service.liveSessions().isEmpty)
        #expect(await fixture.host.canInject() == false)
        #expect(await fixture.host.isCaptureActive == false)
    }

    @Test func theKillHotkeyIsHardToHitByAccident() {
        let hotkey = ScreenShareKillHotkey.standard
        #expect(hotkey.matches(keyCode: 40, modifiers: [.control, .option, .command]))
        // Force Quit's chord must not fire it, nor a bare letter.
        #expect(!hotkey.matches(keyCode: 53, modifiers: [.option, .command]))
        #expect(!hotkey.matches(keyCode: 40, modifiers: [.command]))
        #expect(!hotkey.matches(keyCode: 40, modifiers: []))
    }

    // MARK: - The input route

    @Test func aViewOnlyPhoneCannotInject() async throws {
        let fixture = makeFixture(); defer { cleanUp(fixture) }
        try await fixture.service.setAllowed(deviceId: Self.viewer, allowed: true)
        try await fixture.service.setGrant(deviceId: Self.viewer, grant: .view)
        _ = await fixture.service.join(
            sessionId: "s1", deviceId: Self.viewer, mode: .view,
            controlChallenge: nil, controlSignature: nil, surface: Peer().surface())

        let result = await fixture.service.deliver(.move(.init(displayId: 1, x: 0.5, y: 0.5)), sessionId: "s1")
        #expect(result == .failure(.notControlSession))
        #expect(fixture.input.calls.isEmpty)
        // A session id nobody holds is refused the same way.
        #expect(await fixture.service.deliver(.text("hello"), sessionId: "nope") == .failure(.notControlSession))
    }

    @Test func admittedInputLandsInsideTheDisplayBounds() async {
        let fixture = makeFixture(); defer { cleanUp(fixture) }
        _ = await joinControl(fixture, peer: Peer())

        let onMain = await fixture.service.deliver(.move(.init(displayId: 1, x: 0.5, y: 0.5)), sessionId: "s1")
        #expect((try? onMain.get()) == ScreenShareResolvedPoint(
            displayId: 1, position: CGPoint(x: 960, y: 540), fellBackToMain: false))
        // The second display's bounds start where the main display ends.
        let onSecond = await fixture.service.deliver(
            .click(.init(displayId: 2, x: 0, y: 1), button: .right, clickCount: 2), sessionId: "s1")
        #expect((try? onSecond.get()) == ScreenShareResolvedPoint(
            displayId: 2, position: CGPoint(x: 1920, y: 800), fellBackToMain: false))
        #expect(fixture.input.calls == ["move 960.0,540.0 display 1", "click right x2 1920.0,800.0 display 2"])
    }

    @Test func aDisplayUnpluggedMidSessionFallsBackToTheMainDisplay() async {
        let fixture = makeFixture(); defer { cleanUp(fixture) }
        _ = await joinControl(fixture, peer: Peer())
        fixture.displays.unplug(2)

        let result = await fixture.service.deliver(
            .scroll(.init(displayId: 2, x: 1, y: 0), deltaX: 0, deltaY: -3), sessionId: "s1")
        let resolved = try? result.get()
        #expect(resolved?.displayId == 1)
        #expect(resolved?.fellBackToMain == true)
        #expect(resolved?.position == CGPoint(x: 1920, y: 0))

        // With no display at all there is nowhere to put the pointer.
        fixture.displays.unplug(1)
        #expect(await fixture.service.deliver(
            .move(.init(displayId: 1, x: 0.5, y: 0.5)), sessionId: "s1") == .failure(.noDisplay))
    }

    @Test func theLockScreenAndSecureInputStopFramesAndRejectInjection() async {
        let fixture = makeFixture(); defer { cleanUp(fixture) }
        let peer = Peer()
        _ = await joinControl(fixture, peer: peer)
        #expect(await fixture.host.isCaptureActive)

        fixture.environment.locked = true
        await fixture.service.refreshEnvironment()
        #expect(await fixture.service.deliver(.text("비밀"), sessionId: "s1") == .failure(.blocked))
        #expect(await fixture.host.isCaptureActive == false)
        #expect(peer.events.contains("capture-stopped"))
        #expect(await fixture.service.indicator().framesPaused)

        fixture.environment.locked = false
        fixture.environment.secureInput = true
        await fixture.service.refreshEnvironment()
        #expect(await fixture.service.deliver(.text("비밀"), sessionId: "s1") == .failure(.blocked))
        #expect(await fixture.host.isCaptureActive == false)

        fixture.environment.secureInput = false
        await fixture.service.refreshEnvironment()
        #expect(await fixture.host.isCaptureActive)
        #expect(await fixture.service.deliver(.key(code: 36, modifiers: []), sessionId: "s1").isSuccess)
        // Nothing reached the sink while either state held.
        #expect(fixture.input.calls == ["key 36 modifiers 0"])
    }

    @Test func localHidActivityPausesRemoteInputForTwoSeconds() async {
        let fixture = makeFixture(); defer { cleanUp(fixture) }
        _ = await joinControl(fixture, peer: Peer())

        await fixture.service.localHIDActivity()
        #expect(await fixture.service.deliver(
            .move(.init(displayId: 1, x: 0.1, y: 0.1)), sessionId: "s1") == .failure(.blocked))
        fixture.clock.advance(by: 1.5)
        #expect(await fixture.service.deliver(
            .move(.init(displayId: 1, x: 0.1, y: 0.1)), sessionId: "s1") == .failure(.blocked))
        fixture.clock.advance(by: 0.6)
        #expect(await fixture.service.deliver(
            .move(.init(displayId: 1, x: 0.1, y: 0.1)), sessionId: "s1").isSuccess)
        #expect(fixture.input.calls.count == 1)
    }

    @Test func noKeystrokeEverReachesTheSessionLog() async {
        let fixture = makeFixture(); defer { cleanUp(fixture) }
        _ = await joinControl(fixture, peer: Peer())
        let secret = "비밀번호 hunter2"

        #expect(await fixture.service.deliver(.text(secret), sessionId: "s1").isSuccess)
        #expect(await fixture.service.deliver(.key(code: 42, modifiers: [.command]), sessionId: "s1").isSuccess)
        await fixture.service.killSwitch()

        // The sink received the text — so it really was injected — and the log
        // still only knows the device, the mode and the two timestamps.
        #expect(fixture.input.calls.contains("text \(secret)"))
        let entries = await fixture.service.sessionLog()
        #expect(entries.count == 1)
        let dumped = entries.map { String(describing: $0) }.joined()
        #expect(!dumped.contains("hunter2"))
        #expect(!dumped.contains("비밀"))
        #expect(!dumped.contains("42"))
        #expect(dumped.contains(Self.phone))
        #expect(entries[0].mode == .control)
        #expect(entries[0].endedAt != nil)
    }

    // MARK: - Indicator

    @Test func theIndicatorKnowsWhoIsWatchingAndWhoIsControlling() async throws {
        let fixture = makeFixture(); defer { cleanUp(fixture) }
        #expect(await fixture.service.indicator().isActive == false)
        _ = await joinControl(fixture, peer: Peer())
        try await fixture.service.setAllowed(deviceId: Self.viewer, allowed: true)
        try await fixture.service.setGrant(deviceId: Self.viewer, grant: .view)
        _ = await fixture.service.join(
            sessionId: "s2", deviceId: Self.viewer, mode: .view,
            controlChallenge: nil, controlSignature: nil, surface: Peer().surface())

        let state = await fixture.service.indicator()
        #expect(state.isActive && state.controlling)
        #expect(state.sessions.count == 2)
        #expect(state.framesPaused == false)

        await fixture.service.endSession(sessionId: "s1")
        let afterController = await fixture.service.indicator()
        #expect(afterController.isActive && afterController.controlling == false)
    }

    // MARK: - The paths the rest of the app already has

    @Test func revokingAPhoneInMobileSettingsStopsItsScreenShare() async throws {
        let fixture = makeFixture(); defer { cleanUp(fixture) }
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("mobile-remote-" + UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let clientId = "cGhvbmUtb25lLTAwMDAwMDA"
        let registry = MobileDeviceRegistry(url: directory.appendingPathComponent("devices.json"))
        guard case .issued = registry.issueToken(clientId: clientId, name: "Pixel") else {
            Issue.record("토큰을 발급하지 못했습니다."); return
        }
        let remote = MobileRemoteService(dataDirectory: directory, hostName: "Test Mac", defaultRelayURL: "", watchesNetwork: false)
        await remote.attachScreenShare(fixture.service)
        _ = try await remote.loadOrCreateKey()

        let peer = Peer()
        peer.relayUp = false
        let (_, joined) = await joinControl(fixture, deviceId: clientId, peer: peer)
        #expect(isSuccess(joined))
        #expect(await fixture.host.canInject())

        _ = try await remote.revokeDevice(clientId)

        // The revoke in mobile settings reached the screen-share host: the
        // session is gone, capture is off and the row is no longer allowed.
        #expect(await fixture.service.liveSessions().isEmpty)
        #expect(await fixture.host.canInject() == false)
        #expect(await fixture.host.isCaptureActive == false)
        #expect(await fixture.service.settings(for: clientId) == nil)
        #expect(peer.events.contains("peer-closed"))
        await remote.shutdown()
    }

    @Test func regeneratingTheMobileKeyStopsEveryScreenShareSession() async throws {
        let fixture = makeFixture(); defer { cleanUp(fixture) }
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("mobile-remote-" + UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let remote = MobileRemoteService(dataDirectory: directory, hostName: "Test Mac", defaultRelayURL: "", watchesNetwork: false)
        await remote.attachScreenShare(fixture.service)
        _ = try await remote.loadOrCreateKey()

        let peer = Peer()
        _ = await joinControl(fixture, peer: peer)
        #expect(await fixture.service.liveSessions().count == 1)

        _ = try await remote.regenerateKey()

        #expect(await waitUntil { await fixture.service.liveSessions().isEmpty })
        #expect(await fixture.host.canInject() == false)
        #expect(await fixture.service.allowList().isEmpty)
        await remote.shutdown()
    }

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

private extension Result where Success == ScreenShareResolvedPoint?, Failure == ScreenShareInputRejection {
    var isSuccess: Bool { if case .success = self { return true }; return false }
}

/// A display layout the test rearranges, including unplugging a screen while a
/// session is live.
final class FakeDisplays: ScreenShareDisplaySource, @unchecked Sendable {
    private let lock = NSLock()
    private var layout: [UInt32: CGRect]
    private var main: UInt32

    init(layout: [UInt32: CGRect], main: UInt32) { self.layout = layout; self.main = main }

    func unplug(_ displayId: UInt32) {
        lock.lock(); layout.removeValue(forKey: displayId); lock.unlock()
    }

    func activeDisplayIds() -> [UInt32] {
        lock.lock(); defer { lock.unlock() }
        return layout.keys.sorted { left, _ in left == main }
    }
    func bounds(of displayId: UInt32) -> CGRect? {
        lock.lock(); defer { lock.unlock() }
        return layout[displayId]
    }
    func mainDisplayId() -> UInt32 { lock.lock(); defer { lock.unlock() }; return main }
}

/// Records what the host admitted, in the order it admitted it. Standing in for
/// `CGEventPost`.
final class FakeInput: ScreenShareInputSink, @unchecked Sendable {
    private let lock = NSLock()
    private var recorded: [String] = []
    var calls: [String] { lock.lock(); defer { lock.unlock() }; return recorded }
    private func add(_ line: String) { lock.lock(); recorded.append(line); lock.unlock() }

    func move(to position: CGPoint, displayId: UInt32) async {
        add("move \(position.x),\(position.y) display \(displayId)")
    }
    func click(at position: CGPoint, displayId: UInt32, button: ScreenShareMouseButton, clickCount: Int) async {
        add("click \(button.rawValue) x\(clickCount) \(position.x),\(position.y) display \(displayId)")
    }
    func scroll(at position: CGPoint, displayId: UInt32, deltaX: Int32, deltaY: Int32) async {
        add("scroll \(deltaX),\(deltaY) at \(position.x),\(position.y) display \(displayId)")
    }
    func commitText(_ text: String) async { add("text \(text)") }
    func key(code: UInt16, modifiers: ScreenShareModifiers) async {
        add("key \(code) modifiers \(modifiers.rawValue)")
    }
}

/// The lock screen and secure input, as the test decides them.
final class FakeEnvironment: ScreenShareEnvironmentProbe, @unchecked Sendable {
    private let lock = NSLock()
    private var lockedValue = false
    private var secureValue = false
    var locked: Bool {
        get { lock.lock(); defer { lock.unlock() }; return lockedValue }
        set { lock.lock(); lockedValue = newValue; lock.unlock() }
    }
    var secureInput: Bool {
        get { lock.lock(); defer { lock.unlock() }; return secureValue }
        set { lock.lock(); secureValue = newValue; lock.unlock() }
    }
    func screenLocked() -> Bool { locked }
    func secureInputActive() -> Bool { secureInput }
}
