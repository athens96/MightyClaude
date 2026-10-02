import CoreGraphics
import CoreVideo
import Foundation
import Testing
@testable import MightyCore

/// Proves the Mac runs screen sharing end to end: the `/m1/screen-share` routes,
/// the offer/answer/ICE/end/grant/kill signalling, relay-minted TURN credentials
/// renewed with an ICE restart, the capture plan (idle frames dropped, zoom over a
/// low-res overview), the codec policy and the documented data-channel messages —
/// all with a fake peer, so none of it needs a display, a relay or libwebrtc.
struct ScreenShareEngineTests {
    typealias Keystore = ScreenShareSafetyTests.FakeKeystore

    private static let phone = "phone-one-00000001"
    private static let viewer = "phone-two-00000002"

    // MARK: Fixture

    private struct Fixture {
        let directory: URL
        let service: ScreenShareService
        let engine: ScreenShareEngine
        let peers: FakePeerFactory
        let signals: RecordingSignalSender
        let turn: FakeTurnSource
        let capture: RecordingCaptureBackend
        let pasteboard: FakePasteboard
        let confirmer: FakeConfirmer
        let displays: FakeDisplays
        let environment: FakeEnvironment
        let input: FakeInput
    }

    private func makeFixture(
        layout: [UInt32: CGRect] = [1: CGRect(x: 0, y: 0, width: 1920, height: 1080),
                                    2: CGRect(x: 1920, y: 0, width: 1280, height: 800)],
        main: UInt32 = 1,
        confirm: Bool = true,
        load: ScreenShareMachineLoad = FakeLoad(headroom: 0.9, thermal: false),
        fast: Set<TimeInterval> = [],
        host: ScreenShareHost = ScreenShareHost(),
        confirmer custom: ScreenShareControlKeyConfirmer? = nil,
        tapMarker: ScreenShareTapMarkerSurface? = nil
    ) async -> Fixture {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("screen-engine-" + UUID().uuidString, isDirectory: true)
        let store = ScreenShareSettingsStore(url: directory.appendingPathComponent("screen-share.json"))
        let displays = FakeDisplays(layout: layout, main: main)
        let input = FakeInput()
        let environment = FakeEnvironment()
        let service = ScreenShareService(store: store, host: host, displays: displays, input: input,
                                        environment: environment)
        let peers = FakePeerFactory()
        let signals = RecordingSignalSender()
        let turn = FakeTurnSource()
        let backend = RecordingCaptureBackend()
        let capture = ScreenShareCaptureController(backend: backend, displays: displays)
        let pasteboard = FakePasteboard()
        let confirmer = FakeConfirmer(answer: confirm)
        let engine = ScreenShareEngine(
            service: service, peers: peers, capture: capture, signals: signals, turn: turn,
            displays: displays, pasteboard: pasteboard, confirmer: custom ?? confirmer,
            tapMarker: tapMarker, load: load,
            compressor: nil,
            // The intervals a test names run in a millisecond; every other timer
            // (renewal, connect deadline, keepalive) waits an hour, so it cannot
            // fire in the middle of a test that did not ask for it.
            sleeper: { seconds in
                try await Task.sleep(nanoseconds: fast.contains(seconds) ? 1_000_000 : 3_600_000_000_000)
            })
        // Wired the way the app wires them: the host's own stops and the lock
        // screen / secure input reach the engine.
        await service.observeStops { [engine] stopped in await engine.hostStopped(stopped) }
        await service.observeFrameBlock { [engine] blocked, reason in
            await engine.framesBlockedChanged(blocked, reason: reason)
        }
        return Fixture(directory: directory, service: service, engine: engine, peers: peers,
                       signals: signals, turn: turn, capture: backend, pasteboard: pasteboard,
                       confirmer: confirmer, displays: displays, environment: environment, input: input)
    }

    private func cleanUp(_ fixture: Fixture) {
        try? FileManager.default.removeItem(at: fixture.directory)
    }

    /// Allow-lists a phone with `grant` and, for control, enrols a key the Mac
    /// user confirmed.
    @discardableResult
    private func allow(
        _ fixture: Fixture, deviceId: String = phone, grant: ScreenShareGrant, keystore: Keystore? = nil
    ) async throws -> Keystore? {
        try await fixture.service.setAllowed(deviceId: deviceId, allowed: true)
        try await fixture.service.setGrant(deviceId: deviceId, grant: grant)
        guard grant == .control else { return nil }
        let keystore = keystore ?? Keystore()
        let enrolled = await fixture.engine.enrolControlKey(
            deviceId: deviceId, publicKeyB64: keystore.publicKeyData.base64EncodedString())
        #expect(enrolled.isSuccess)
        return keystore
    }

    /// Starts a session the way the route does: read `state`, sign the challenge
    /// it carries, then POST it back.
    private func startSession(
        _ fixture: Fixture, deviceId: String = phone, mode: ScreenShareGrant,
        keystore: Keystore? = nil, displayId: UInt32? = nil,
        network: String = "wifi", decodes: [String] = ["H264"], sendOffer: Bool = true
    ) async -> Result<ScreenShareSessionReply, ScreenShareRouteRefusal> {
        var signature: String?
        if mode == .control {
            guard case .success(let state) = await fixture.engine.state(deviceId: deviceId),
                  let raw = state.screenShare.controlChallengeB64,
                  let challenge = Data(base64Encoded: raw), let keystore
            else { return .failure(.controlSignature) }
            signature = keystore.sign(challenge: challenge).base64EncodedString()
        }
        let request = decodeRequest([
            "mode": mode.rawValue,
            "displayId": displayId ?? 1,
            "controlSignatureB64": signature ?? "",
            "network": network,
            "decodes": decodes,
        ])
        let result = await fixture.engine.start(deviceId: deviceId, request: request)
        // What the route does once its reply has left.
        if sendOffer, case .success(let reply) = result {
            await fixture.engine.sendInitialOffer(sessionId: reply.sessionId)
        }
        return result
    }

    private func decodeRequest(_ object: [String: Any]) -> ScreenShareSessionRequestBody {
        var object = object
        if (object["controlSignatureB64"] as? String)?.isEmpty == true { object["controlSignatureB64"] = nil }
        let data = try! JSONSerialization.data(withJSONObject: object)
        return try! JSONDecoder().decode(ScreenShareSessionRequestBody.self, from: data)
    }

    // MARK: Capability

    @Test func screenShareIsNotInTheBaseCapabilityList() {
        // Advertised only by a host whose engine is attached (see the route tests).
        #expect(!MobileCapability.all.contains(MobileCapability.screenShare))
    }

    // MARK: Routes

    @Test func stateHidesEverythingFromAPhoneTheMacHasNotAllowed() async throws {
        let fixture = await makeFixture()
        defer { cleanUp(fixture) }
        guard case .success(let state) = await fixture.engine.state(deviceId: Self.phone) else {
            Issue.record("state refused an allow-listed phone"); return
        }
        #expect(state.screenShare.allowed == false)
        #expect(state.screenShare.grant == "none")
        #expect(state.screenShare.isBeta)
        // No challenge, no TURN credential and no enrolled key for a phone that
        // may not be here: a refused phone learns nothing it could reuse.
        #expect(state.screenShare.controlChallengeB64 == nil)
        #expect(state.screenShare.iceServers == nil)
        #expect(state.screenShare.controlKeyFingerprint == nil)
        #expect(state.screenShare.displays.count == 2)
        #expect(state.screenShare.displays.first?.main == true)
    }

    @Test func aLegacyPhoneWithoutAClientIdIsRefusedWithAReason() async {
        let fixture = await makeFixture()
        defer { cleanUp(fixture) }
        if case .failure(let refusal) = await fixture.engine.state(deviceId: MobileDeviceRegistry.legacyId) {
            #expect(refusal == .legacyClient)
        } else { Issue.record("a legacy phone was given screen-share state") }
        let started = await startSession(fixture, deviceId: MobileDeviceRegistry.legacyId, mode: .view)
        #expect(started.refusal == .legacyClient)
    }

    @Test func stateCarriesTheChallengeAndTheRelayMintedTurnCredentialOnceControlIsGranted() async throws {
        let fixture = await makeFixture()
        defer { cleanUp(fixture) }
        _ = try await allow(fixture, grant: .control)
        guard case .success(let state) = await fixture.engine.state(deviceId: Self.phone) else {
            Issue.record("state refused an allow-listed phone"); return
        }
        #expect(state.screenShare.grant == "control")
        #expect(state.screenShare.controlChallengeB64 != nil)
        #expect(state.screenShare.controlKeyFingerprint != nil)
        #expect(state.screenShare.idleTimeoutSeconds == 600)
        // The TURN credential is the relay's, and it carries STUN beside it so a
        // phone off the Wi-Fi can still find a reflexive address.
        let servers = try #require(state.screenShare.iceServers)
        #expect(servers.contains { $0.urls.contains { $0.hasPrefix("stun:") } })
        let turn = try #require(servers.first { $0.urls.contains { $0.hasPrefix("turn:") } })
        #expect(turn.username == "1:abcd")
        #expect(turn.credential == "secret-hmac-1")
        // The coturn shared secret is not in any of it.
        #expect(!servers.contains { ($0.credential ?? "").contains("coturn-shared") })
    }

    @Test func aViewSessionStartsCaptureAndSendsOneOffer() async throws {
        let fixture = await makeFixture()
        defer { cleanUp(fixture) }
        _ = try await allow(fixture, grant: .view)
        guard case .success(let reply) = await startSession(fixture, mode: .view) else {
            Issue.record("an allowed view session was refused"); return
        }
        #expect(reply.mode == "view")
        #expect(reply.displayId == 1)
        #expect(reply.codec == "H264")
        #expect(reply.quality == ScreenShareQualityProfile(width: 1920, height: 1080, fps: 30, maxBitrateKbps: 6000))
        // Capture really started, on the display that was asked for.
        let plan = try #require(await fixture.capture.lastPlan)
        #expect(plan.displayId == 1)
        #expect(plan.layers.count == 1)
        #expect(plan.primary?.width == 1920)
        // And the offer went out, with the screen-content settings in the body.
        let offer = try #require(await fixture.signals.sent.first)
        #expect(offer.type == "screen-offer")
        guard case .offer(let sessionId, _, let mode, let displayId, let codec, _, let iceRestart) = offer else {
            Issue.record("the first frame was not an offer"); return
        }
        #expect(sessionId == reply.sessionId)
        #expect(mode == .view)
        #expect(displayId == 1)
        #expect(codec == .h264)
        #expect(iceRestart == false)
    }

    @Test func aControlSessionNeedsASignatureTheMacVerifies() async throws {
        let fixture = await makeFixture()
        defer { cleanUp(fixture) }
        let keystore = try #require(try await allow(fixture, grant: .control))

        // A phone that signs with another key is refused, and the challenge is
        // spent either way: the next attempt has to ask for a new one.
        guard case .success(let state) = await fixture.engine.state(deviceId: Self.phone),
              let raw = state.screenShare.controlChallengeB64,
              let challenge = Data(base64Encoded: raw) else { Issue.record("no challenge"); return }
        let forged = Keystore().sign(challenge: challenge).base64EncodedString()
        let refused = await fixture.engine.start(deviceId: Self.phone, request: decodeRequest([
            "mode": "control", "displayId": 1, "controlSignatureB64": forged, "network": "wifi",
        ]))
        #expect(refused.refusal == .controlSignature)

        // Replaying the same challenge, now with the right key, still fails: the
        // challenge was spent by the attempt above.
        let replayed = await fixture.engine.start(deviceId: Self.phone, request: decodeRequest([
            "mode": "control", "displayId": 1,
            "controlSignatureB64": keystore.sign(challenge: challenge).base64EncodedString(),
            "network": "wifi",
        ]))
        #expect(replayed.refusal == .controlSignature)

        // A fresh challenge signed by the enrolled key does start a session.
        guard case .success(let reply) = await startSession(fixture, mode: .control, keystore: keystore) else {
            Issue.record("a correctly signed control session was refused"); return
        }
        #expect(reply.mode == "control")
    }

    @Test func aPhoneWithOnlyViewCannotStartControl() async throws {
        let fixture = await makeFixture()
        defer { cleanUp(fixture) }
        _ = try await allow(fixture, grant: .view)
        let started = await startSession(fixture, mode: .control, keystore: Keystore())
        #expect(started.refusal == .controlSignature)
    }

    @Test func oneControllerAndTwoViewersIsTheLimit() async throws {
        let fixture = await makeFixture()
        defer { cleanUp(fixture) }
        _ = try await allow(fixture, grant: .view)
        _ = try await allow(fixture, deviceId: Self.viewer, grant: .view)
        #expect(await startSession(fixture, mode: .view).isSuccess)
        #expect(await startSession(fixture, deviceId: Self.viewer, mode: .view).isSuccess)
        // A third viewer has nowhere to sit.
        let third = await startSession(fixture, mode: .view)
        #expect(third.refusal == .concurrencyLimit)
    }

    // MARK: Control-key enrolment

    @Test func aControlKeyIsStoredOnlyOnceAndOnlyWhenTheMacUserConfirmsIt() async throws {
        let fixture = await makeFixture()
        defer { cleanUp(fixture) }
        try await fixture.service.setAllowed(deviceId: Self.phone, allowed: true)
        try await fixture.service.setGrant(deviceId: Self.phone, grant: .control)
        let first = Keystore()
        let stored = await fixture.engine.enrolControlKey(
            deviceId: Self.phone, publicKeyB64: first.publicKeyData.base64EncodedString())
        guard case .success(let reply) = stored else { Issue.record("a confirmed key was refused"); return }
        #expect(await fixture.confirmer.asked == 1)
        #expect(reply.fingerprint == ScreenShareControlKey.fingerprint(first.publicKeyData))
        #expect(await fixture.service.settings(for: Self.phone)?.controlKeyPublicData == first.publicKeyData)

        // The same key again is the same answer — a retried request is not a
        // replacement — and it does not ask the user a second time.
        let again = await fixture.engine.enrolControlKey(
            deviceId: Self.phone, publicKeyB64: first.publicKeyData.base64EncodedString())
        #expect(again.isSuccess)
        #expect(await fixture.confirmer.asked == 1)

        // A different key is refused outright: no silent replacement.
        let second = Keystore()
        let replaced = await fixture.engine.enrolControlKey(
            deviceId: Self.phone, publicKeyB64: second.publicKeyData.base64EncodedString())
        #expect(replaced.refusal == .controlKeyPresent)
        #expect(await fixture.service.settings(for: Self.phone)?.controlKeyPublicData == first.publicKeyData)
    }

    @Test func aKeyTheMacUserRejectsIsNotStored() async throws {
        let fixture = await makeFixture(confirm: false)
        defer { cleanUp(fixture) }
        try await fixture.service.setAllowed(deviceId: Self.phone, allowed: true)
        try await fixture.service.setGrant(deviceId: Self.phone, grant: .control)
        let refused = await fixture.engine.enrolControlKey(
            deviceId: Self.phone, publicKeyB64: Keystore().publicKeyData.base64EncodedString())
        #expect(refused.refusal == .controlKeyNotConfirmed)
        #expect(await fixture.service.settings(for: Self.phone)?.controlKeyPublicData == nil)
    }

    @Test func enrolmentNeedsTheAllowListAndTheControlGrantFirst() async throws {
        let fixture = await makeFixture()
        defer { cleanUp(fixture) }
        let key = Keystore().publicKeyData.base64EncodedString()
        #expect(await fixture.engine.enrolControlKey(deviceId: Self.phone, publicKeyB64: key).refusal == .deviceNotAllowed)
        try await fixture.service.setAllowed(deviceId: Self.phone, allowed: true)
        try await fixture.service.setGrant(deviceId: Self.phone, grant: .view)
        #expect(await fixture.engine.enrolControlKey(deviceId: Self.phone, publicKeyB64: key).refusal == .insufficientGrant)
        // Not a P-256 point at all.
        try await fixture.service.setGrant(deviceId: Self.phone, grant: .control)
        #expect(await fixture.engine.enrolControlKey(
            deviceId: Self.phone, publicKeyB64: Data(repeating: 7, count: 65).base64EncodedString()).refusal == .badRequest)
        #expect(await fixture.confirmer.asked == 0)
    }

    // MARK: Signalling

    @Test func theAnswerAndTrickledCandidatesReachThePeerAndTheEndStopsTheSession() async throws {
        let fixture = await makeFixture()
        defer { cleanUp(fixture) }
        _ = try await allow(fixture, grant: .view)
        guard case .success(let reply) = await startSession(fixture, mode: .view) else {
            Issue.record("start refused"); return
        }
        let peer = try #require(await fixture.peers.peer(sessionId: reply.sessionId))

        await fixture.engine.handle(.answer(sessionId: reply.sessionId, sdp: "v=0 answer"), from: Self.phone)
        #expect(await peer.answer == "v=0 answer")

        await fixture.engine.handle(.ice(sessionId: reply.sessionId, candidate: "candidate:1 host",
                                        sdpMid: "0", sdpMLineIndex: 0, usernameFragment: nil), from: Self.phone)
        #expect(await peer.remoteCandidates == ["candidate:1 host"])

        // A candidate the peer gathers is trickled out, one per frame.
        await peer.emitLocalCandidate("candidate:2 srflx")
        #expect(await fixture.signals.sent.contains { $0.type == "screen-ice" })

        // A frame naming a session this phone does not own is dropped.
        await fixture.engine.handle(.answer(sessionId: "not-mine", sdp: "x"), from: Self.phone)
        await fixture.engine.handle(.answer(sessionId: reply.sessionId, sdp: "second"), from: Self.viewer)
        #expect(await peer.answer == "v=0 answer")

        await fixture.engine.handle(.sessionEnd(sessionId: reply.sessionId, reason: "user-stop"), from: Self.phone)
        // The host closed the peer through the surface it was given at join.
        #expect(await peer.isClosed)
        #expect(await fixture.engine.liveSessionIds.isEmpty)
        #expect(await fixture.service.liveSessions().isEmpty)
        #expect(await fixture.signals.sent.contains { $0.type == "screen-session-end" })
    }

    @Test func aPhoneCannotPoseAsTheMacsOwnSignalling() {
        // Only the three client-to-host types parse at all; a phone echoing a
        // host-only type back gets nowhere.
        #expect(ScreenShareSignal.inbound(["type": "screen-answer", "sessionId": "s", "sdp": "v=0"]) != nil)
        #expect(ScreenShareSignal.inbound(["type": "screen-offer", "sessionId": "s", "sdp": "v=0"]) == nil)
        #expect(ScreenShareSignal.inbound(["type": "screen-kill", "sessionId": "s", "reason": "revoked"]) == nil)
        #expect(ScreenShareSignal.inbound(["type": "screen-grant", "sessionId": "s", "allowed": true]) == nil)
        // An unknown end reason is refused rather than defaulted.
        #expect(ScreenShareSignal.inbound(["type": "screen-session-end", "sessionId": "s", "reason": "whatever"]) == nil)
        // An empty candidate is the end-of-candidates marker, not a malformed one.
        #expect(ScreenShareSignal.inbound(["type": "screen-ice", "sessionId": "s", "candidate": ""]) != nil)
    }

    @Test func everySignallingFrameFitsTheRelayContract() throws {
        let quality = ScreenShareQualityProfile(width: 1920, height: 1080, fps: 30, maxBitrateKbps: 6000)
        let offer = ScreenShareSignal.offer(
            sessionId: "s1", sdp: String(repeating: "a=x\r\n", count: 200), mode: .control,
            displayId: 1, codec: .h264, quality: quality, iceRestart: true)
        let encoded = try #require(offer.encoded())
        #expect(encoded.count <= ScreenShareSignal.maximumPlaintextBytes)
        let object = try #require(try JSONSerialization.jsonObject(with: encoded) as? [String: Any])
        #expect(object["type"] as? String == "screen-offer")
        #expect(object["iceRestart"] as? Bool == true)
        #expect((object["quality"] as? [String: Any])?["maxBitrateKbps"] as? Int == 6000)
        // An SDP larger than the contract allows is refused rather than truncated.
        let huge = ScreenShareSignal.offer(
            sessionId: "s1", sdp: String(repeating: "a", count: 70_000), mode: .view,
            displayId: 1, codec: .h264, quality: quality, iceRestart: false)
        #expect(huge.encoded() == nil)
    }

    // MARK: TURN renewal

    @Test func aRenewedCredentialReachesThePeerBeforeTheIceRestartThatUsesIt() async throws {
        let fixture = await makeFixture()
        defer { cleanUp(fixture) }
        _ = try await allow(fixture, grant: .view)
        guard case .success(let reply) = await startSession(fixture, mode: .view) else {
            Issue.record("start refused"); return
        }
        let peer = try #require(await fixture.peers.peer(sessionId: reply.sessionId))
        await fixture.signals.clear()

        await fixture.engine.renewCredential(sessionId: reply.sessionId)

        // The peer has the renewed servers, and it got them before the restart
        // gathered against them.
        let applied = try #require(await peer.configuredServers.last)
        #expect(applied.contains { $0.credential == "secret-hmac-2" })
        #expect(await peer.offerLog.last?.iceRestart == true)
        #expect(await peer.configurationBeforeRestart)

        // And the phone was handed them in a grant, then the restart offer.
        let frames = await fixture.signals.sent
        let grantIndex = try #require(frames.firstIndex { $0.type == "screen-grant" })
        let offerIndex = try #require(frames.firstIndex { $0.type == "screen-offer" })
        #expect(grantIndex < offerIndex)
        guard case .grant(_, _, _, _, let servers, _) = frames[grantIndex] else {
            Issue.record("the grant carried no ICE servers"); return
        }
        #expect(servers?.contains { $0.credential == "secret-hmac-2" } == true)
        guard case .offer(_, _, _, _, _, _, let iceRestart) = frames[offerIndex] else {
            Issue.record("not an offer"); return
        }
        #expect(iceRestart)
    }

    @Test func aCredentialIsRenewedOnlyWhenItIsCloseToExpiry() {
        let issued = Date(timeIntervalSinceReferenceDate: 0)
        let credential = ScreenShareTurnCredential(
            username: "1:abcd", password: "p", uris: ["turn:relay.example:3478?transport=udp"],
            ttl: 3600, issuedAt: issued)
        #expect(!credential.needsRenewal(now: issued))
        #expect(!credential.needsRenewal(now: issued.addingTimeInterval(3000)))
        #expect(credential.needsRenewal(now: issued.addingTimeInterval(3600 - 299)))
        #expect(credential.isExpired(now: issued.addingTimeInterval(3600)))
        #expect(ScreenShareTurnCredential.stunURI("turn:relay.example:3478?transport=udp") == "stun:relay.example:3478")
        #expect(ScreenShareTurnCredential.stunURI("stun:relay.example:3478") == nil)
        // A frame without a usable TURN URI is not a credential.
        #expect(ScreenShareTurnCredential.parse(
            relayFrame: ["username": "u", "password": "p", "uris": ["http://nope"], "ttl": 10],
            now: issued) == nil)
    }

    @Test func aRelayThatRefusesTurnDoesNotStopASession() async throws {
        let fixture = await makeFixture()
        defer { cleanUp(fixture) }
        await fixture.turn.setUnavailable(true)
        _ = try await allow(fixture, grant: .view)
        let started = await startSession(fixture, mode: .view)
        #expect(started.isSuccess)
        // Host and reflexive candidates still work; there is simply no TURN pair.
        guard case .success(let state) = await fixture.engine.state(deviceId: Self.phone) else {
            Issue.record("state refused"); return
        }
        #expect(state.screenShare.iceServers == nil)
    }

    // MARK: Codec policy

    @Test func hardwareH264IsTheDefaultAndSoftwareCodecsNeedHeadroom() {
        let wifi = ScreenShareCodecConditions(network: .wifi, phoneDecodes: [.h264, .vp9, .av1],
                                              cpuHeadroom: 0.9, thermalPressure: false)
        #expect(ScreenShareCodecPolicy.choose(wifi) == .h264)
        let cellular = ScreenShareCodecConditions(network: .cellular, phoneDecodes: [.h264, .vp9, .av1],
                                                  cpuHeadroom: 0.9, thermalPressure: false)
        #expect(ScreenShareCodecPolicy.choose(cellular) == .av1)
        let noAV1 = ScreenShareCodecConditions(network: .cellular, phoneDecodes: [.h264, .vp9],
                                               cpuHeadroom: 0.9, thermalPressure: false)
        #expect(ScreenShareCodecPolicy.choose(noAV1) == .vp9)
        let phoneCannotDecode = ScreenShareCodecConditions(network: .cellular, phoneDecodes: [.h264],
                                                           cpuHeadroom: 0.9, thermalPressure: false)
        #expect(ScreenShareCodecPolicy.choose(phoneCannotDecode) == .h264)
        let hot = ScreenShareCodecConditions(network: .cellular, phoneDecodes: [.av1],
                                             cpuHeadroom: 0.9, thermalPressure: true)
        #expect(ScreenShareCodecPolicy.choose(hot) == .h264)
        let busy = ScreenShareCodecConditions(network: .cellular, phoneDecodes: [.av1],
                                              cpuHeadroom: 0.1, thermalPressure: false)
        #expect(ScreenShareCodecPolicy.choose(busy) == .h264)
        // Under pressure a live software stream goes back to hardware H.264.
        #expect(ScreenShareCodecPolicy.mustFallBack(to: .av1, conditions: hot))
        #expect(!ScreenShareCodecPolicy.mustFallBack(to: .h264, conditions: hot))
    }

    @Test func theSessionPicksTheCodecThePolicyChose() async throws {
        let fixture = await makeFixture(load: FakeLoad(headroom: 0.9, thermal: false))
        defer { cleanUp(fixture) }
        _ = try await allow(fixture, grant: .view)
        guard case .success(let reply) = await startSession(
            fixture, mode: .view, network: "cellular", decodes: ["H264", "AV1"]) else {
            Issue.record("start refused"); return
        }
        #expect(reply.codec == "AV1")
        // Mobile data caps at 720p15 and ~1 Mbps.
        #expect(reply.quality.height <= 720)
        #expect(reply.quality.fps == 15)
        #expect(reply.quality.maxBitrateKbps == 1000)
    }

    @Test func quailtyCeilingsFollowTheNetworkAndTheTurnQuota() {
        let wifi = ScreenShareQuality.profile(network: .wifi, displayWidth: 3840, displayHeight: 2160)
        #expect(wifi == ScreenShareQualityProfile(width: 1920, height: 1080, fps: 30, maxBitrateKbps: 6000))
        // A narrow display keeps its own aspect ratio rather than being stretched.
        let tall = ScreenShareQuality.profile(network: .wifi, displayWidth: 1280, displayHeight: 800)
        #expect(tall.width == 1280 && tall.height == 800)
        let cellular = ScreenShareQuality.profile(network: .cellular, displayWidth: 1920, displayHeight: 1080)
        #expect(cellular.maxBitrateKbps == 1000 && cellular.fps == 15 && cellular.height == 720)
        // A relayed path is clamped again to the coturn per-session quota.
        let relayed = ScreenShareQuality.profile(network: .wifi, path: .relay, displayWidth: 1920, displayHeight: 1080)
        #expect(relayed.maxBitrateKbps == ScreenShareQuality.turnSessionQuotaKbps)
        let relayedCellular = ScreenShareQuality.profile(network: .cellular, path: .relay,
                                                         displayWidth: 1920, displayHeight: 1080)
        #expect(relayedCellular.maxBitrateKbps == 1000)
    }

    // MARK: Capture

    @Test func idleFramesAreDroppedSoAStillScreenCostsAlmostNothing() {
        var gate = ScreenShareFrameGate()
        let start = Date(timeIntervalSinceReferenceDate: 0)
        // The first frame always goes: the phone has nothing to show until it does.
        let first = gate.admit(status: .complete, dirtyRects: 0, now: start)
        #expect(first)
        // Nothing changed, so nothing is encoded.
        let unchanged = gate.admit(status: .complete, dirtyRects: 0, now: start.addingTimeInterval(0.1))
        let idle = gate.admit(status: .idle, dirtyRects: 4, now: start.addingTimeInterval(0.2))
        let blank = gate.admit(status: .blank, dirtyRects: 4, now: start.addingTimeInterval(0.3))
        #expect(!unchanged)
        #expect(!idle)
        #expect(!blank)
        #expect(gate.dropped == 3)
        // A changed region is sent at once.
        let changed = gate.admit(status: .complete, dirtyRects: 2, now: start.addingTimeInterval(0.4))
        #expect(changed)
        // A still screen still refreshes once in a while, so a late decoder recovers.
        let keepalive = gate.admit(
            status: .complete, dirtyRects: 0,
            now: start.addingTimeInterval(0.4 + ScreenShareFrameGate.keepaliveInterval))
        #expect(keepalive)
    }

    @Test func zoomStreamsTheRegionAtFullBudgetOverARealLowResOverview() {
        let bounds = CGRect(x: 0, y: 0, width: 1920, height: 1080)
        let quality = ScreenShareQualityProfile(width: 1920, height: 1080, fps: 30, maxBitrateKbps: 6000)
        // Not zoomed: one layer, no overview to pay for.
        let full = ScreenShareCapturePlanner.plan(displayId: 1, displayBounds: bounds,
                                                  region: .full, quality: quality)
        #expect(full.layers.count == 1)
        #expect(full.overview == nil)
        #expect(full.primary?.sourceRect == bounds)

        // Zoomed into a quarter: that quarter now gets the whole pixel budget,
        // which is what keeps terminal text legible, and a cheap whole-screen
        // overview goes under it.
        let region = ScreenShareZoomRegion(x: 0.25, y: 0.25, width: 0.25, height: 0.25)
        let zoomed = ScreenShareCapturePlanner.plan(displayId: 1, displayBounds: bounds,
                                                    region: region, quality: quality)
        let primary = zoomed.primary!
        #expect(primary.sourceRect == CGRect(x: 480, y: 270, width: 480, height: 270))
        #expect(primary.width == 480 && primary.height == 270)
        #expect(primary.fps == 30)
        let overview = zoomed.overview!
        #expect(overview.sourceRect == bounds)
        #expect(overview.width == ScreenShareCapturePlanner.overviewMaxWidth)
        #expect(overview.fps == ScreenShareCapturePlanner.overviewFps)
        // The overview really is low-res: it spends a quarter of a pixel on each
        // display pixel, where the zoomed region gets one for one — which is the
        // whole point of streaming the region separately.
        let overviewDensity = Double(overview.width) / bounds.width
        let primaryDensity = Double(primary.width) / primary.sourceRect.width
        #expect(overviewDensity < primaryDensity)
        #expect(overview.width <= ScreenShareCapturePlanner.overviewMaxWidth)
    }

    @Test func aDegenerateZoomRegionIsRefusedAndClampedInsideTheDisplay() {
        #expect(ScreenShareZoomRegion(x: 0, y: 0, width: 0.001, height: 0.5).normalized == nil)
        #expect(ScreenShareZoomRegion(x: .nan, y: 0, width: 0.5, height: 0.5).normalized?.x == 0)
        let clamped = ScreenShareZoomRegion(x: 0.8, y: 0.8, width: 0.5, height: 0.5).normalized!
        #expect(clamped.x + clamped.width <= 1.0001)
        #expect(clamped.y + clamped.height <= 1.0001)
    }

    @Test func anUnpluggedDisplayFallsBackToTheMainOne() async throws {
        let fixture = await makeFixture()
        defer { cleanUp(fixture) }
        _ = try await allow(fixture, grant: .view)
        guard case .success(let reply) = await startSession(fixture, mode: .view, displayId: 2) else {
            Issue.record("start refused"); return
        }
        #expect(reply.displayId == 2)
        fixture.displays.unplug(2)
        // The phone's display switcher asks for the display that just went away.
        let peer = try #require(await fixture.peers.peer(sessionId: reply.sessionId))
        await peer.emitData(Self.json(["t": "display", "displayId": 2]))
        try await Task.sleep(nanoseconds: 40_000_000)
        let plan = try #require(await fixture.capture.lastPlan)
        #expect(plan.displayId == 1)
    }

    // MARK: Data channel → CGEventPost

    @Test func theDocumentedDataChannelMessagesBecomeInjectedEvents() async throws {
        let fixture = await makeFixture()
        defer { cleanUp(fixture) }
        let keystore = try #require(try await allow(fixture, grant: .control))
        guard case .success(let reply) = await startSession(fixture, mode: .control, keystore: keystore) else {
            Issue.record("control start refused"); return
        }
        let peer = try #require(await fixture.peers.peer(sessionId: reply.sessionId))

        for object in [
            ["t": "tap", "displayId": 1, "x": 0.5, "y": 0.5, "button": "left"],
            ["t": "tap", "displayId": 1, "x": 0.1, "y": 0.2, "button": "right"],
            ["t": "drag", "displayId": 1, "x": 0.3, "y": 0.4, "phase": "begin"],
            ["t": "drag", "displayId": 1, "x": 0.35, "y": 0.45, "phase": "move"],
            ["t": "drag", "displayId": 1, "x": 0.4, "y": 0.5, "phase": "end"],
            ["t": "scroll", "displayId": 1, "x": 0.5, "y": 0.5, "dx": 0.0, "dy": -0.1],
            ["t": "text", "text": "안녕하세요"],
            ["t": "key", "combo": "cmd+v"],
        ] as [[String: Any]] {
            await peer.emitData(Self.json(object))
        }
        try await Task.sleep(nanoseconds: 80_000_000)
        let calls = fixture.input.calls
        #expect(calls.contains { $0.hasPrefix("click left x1 960.0,540.0") })
        #expect(calls.contains { $0.hasPrefix("click right x1 192.0,216.0") })
        #expect(calls.contains { $0.hasPrefix("drag begin") })
        #expect(calls.contains { $0.hasPrefix("drag move") })
        #expect(calls.contains { $0.hasPrefix("drag end") })
        #expect(calls.contains { $0.hasPrefix("scroll 0,-10") })
        // Korean arrives as one committed string, never as jamo keystrokes.
        #expect(calls.contains("text 안녕하세요"))
        // ⌘V is kVK_ANSI_V with the command modifier and nothing else.
        #expect(calls.contains("key 9 modifiers \(ScreenShareModifiers.command.rawValue)"))
    }

    // MARK: Tap marker

    @Test func aTapMayCarryAMarkerIdAndAMalformedOneDropsTheTap() {
        let plain = ScreenShareDataChannel.decode(
            ["t": "tap", "displayId": 1, "x": 0.5, "y": 0.25, "button": "left"])
        let point = ScreenShareNormalizedPoint(displayId: 1, x: 0.5, y: 0.25)
        #expect(plain == .success(.input(.click(point, button: .left, clickCount: 1))))
        let marked = ScreenShareDataChannel.decode(
            ["t": "tap", "displayId": 1, "x": 0.5, "y": 0.25, "button": "left", "marker": "m-12_ab"])
        #expect(marked == .success(.markedTap(.click(point, button: .left, clickCount: 1), markerId: "m-12_ab")))
        for bad in ["", String(repeating: "a", count: 33), "a b", "ㄱ", "a/b", 7, true] as [Any] {
            let decoded = ScreenShareDataChannel.decode(
                ["t": "tap", "displayId": 1, "x": 0.5, "y": 0.25, "button": "left", "marker": bad])
            #expect(decoded == .failure(.malformed), "\(bad) was accepted as a marker id")
        }
        #expect(ScreenShareTapMarker.isValidId(String(repeating: "Z", count: 32)))
    }

    @Test func aMarkedTapClicksDrawsTheMarkerWhereTheHostResolvedItAndEchoesTheId() async throws {
        let marker = RecordingTapMarker()
        let fixture = await makeFixture(tapMarker: marker)
        defer { cleanUp(fixture) }
        let keystore = try #require(try await allow(fixture, grant: .control))
        guard case .success(let state) = await fixture.engine.state(deviceId: Self.phone) else {
            Issue.record("state refused"); return
        }
        #expect(state.screenShare.tapMarker)
        guard case .success(let reply) = await startSession(fixture, mode: .control, keystore: keystore) else {
            Issue.record("control start refused"); return
        }
        let peer = try #require(await fixture.peers.peer(sessionId: reply.sessionId))
        await peer.emitData(Self.json(["t": "tap", "displayId": 2, "x": 0.5, "y": 0.5, "button": "left", "marker": "probe-1"]))
        try await Task.sleep(nanoseconds: 60_000_000)
        // The click itself went through the host, at display 2's centre.
        #expect(fixture.input.calls.contains { $0.hasPrefix("click left x1 2560.0,400.0 display 2") })
        #expect(marker.shown == ["2560.0,400.0 display 2"])
        let echoes = await peer.sentData.compactMap {
            (try? JSONSerialization.jsonObject(with: $0)) as? [String: Any]
        }.filter { $0["t"] as? String == "marker" }
        #expect(echoes.count == 1)
        #expect(echoes.first?["id"] as? String == "probe-1")
        #expect(echoes.first?["shown"] as? Bool == true)
    }

    @Test func aRefusedMarkedTapDrawsNothingAndSaysSo() async throws {
        let marker = RecordingTapMarker()
        let fixture = await makeFixture(tapMarker: marker)
        defer { cleanUp(fixture) }
        _ = try await allow(fixture, grant: .view)
        guard case .success(let reply) = await startSession(fixture, mode: .view) else {
            Issue.record("start refused"); return
        }
        let peer = try #require(await fixture.peers.peer(sessionId: reply.sessionId))
        await peer.emitData(Self.json(["t": "tap", "displayId": 1, "x": 0.5, "y": 0.5, "button": "left", "marker": "v1"]))
        try await Task.sleep(nanoseconds: 60_000_000)
        #expect(fixture.input.calls.isEmpty)
        #expect(marker.shown.isEmpty)
        let echoes = await peer.sentData.compactMap {
            (try? JSONSerialization.jsonObject(with: $0)) as? [String: Any]
        }.filter { $0["t"] as? String == "marker" }
        #expect(echoes.first?["id"] as? String == "v1")
        #expect(echoes.first?["shown"] as? Bool == false)
    }

    @Test func aMacWithoutAMarkerSurfaceDoesNotAdvertiseOne() async throws {
        let fixture = await makeFixture()
        defer { cleanUp(fixture) }
        _ = try await allow(fixture, grant: .view)
        guard case .success(let state) = await fixture.engine.state(deviceId: Self.phone) else {
            Issue.record("state refused"); return
        }
        #expect(!state.screenShare.tapMarker)
        let encoded = try JSONSerialization.jsonObject(with: JSONEncoder().encode(state)) as? [String: Any]
        let body = encoded?["screenShare"] as? [String: Any]
        #expect(body?["tapMarker"] as? Bool == false)
    }

    @Test func theReferenceScenesPhasesReachEveryConnectedPhone() async throws {
        let fixture = await makeFixture()
        defer { cleanUp(fixture) }
        _ = try await allow(fixture, grant: .view)
        guard case .success(let reply) = await startSession(fixture, mode: .view) else {
            Issue.record("start refused"); return
        }
        let peer = try #require(await fixture.peers.peer(sessionId: reply.sessionId))
        // Not connected yet: nothing to tell.
        await fixture.engine.announceScene(.preroll)
        await peer.emitConnected(.host)
        try await Task.sleep(nanoseconds: 30_000_000)
        await fixture.engine.announceScene(.motion)
        await fixture.engine.announceScene(.still)
        let notes = await peer.sentData.compactMap {
            (try? JSONSerialization.jsonObject(with: $0)) as? [String: Any]
        }.filter { $0["t"] as? String == "scene" }.compactMap { $0["phase"] as? String }
        #expect(notes == ["motion", "still"])
    }

    @Test func aViewOnlyPhoneInjectsNothingHoweverItAsks() async throws {
        let fixture = await makeFixture()
        defer { cleanUp(fixture) }
        _ = try await allow(fixture, grant: .view)
        guard case .success(let reply) = await startSession(fixture, mode: .view) else {
            Issue.record("start refused"); return
        }
        let peer = try #require(await fixture.peers.peer(sessionId: reply.sessionId))
        await peer.emitData(Self.json(["t": "tap", "displayId": 1, "x": 0.5, "y": 0.5, "button": "left"]))
        await peer.emitData(Self.json(["t": "text", "text": "rm -rf /"]))
        try await Task.sleep(nanoseconds: 60_000_000)
        #expect(fixture.input.calls.isEmpty)
    }

    @Test func aPasswordFieldStopsInjectionEvenInAControlSession() async throws {
        let fixture = await makeFixture()
        defer { cleanUp(fixture) }
        let keystore = try #require(try await allow(fixture, grant: .control))
        guard case .success(let reply) = await startSession(fixture, mode: .control, keystore: keystore) else {
            Issue.record("control start refused"); return
        }
        let peer = try #require(await fixture.peers.peer(sessionId: reply.sessionId))
        fixture.environment.secureInput = true
        await peer.emitData(Self.json(["t": "text", "text": "secret"]))
        try await Task.sleep(nanoseconds: 60_000_000)
        #expect(fixture.input.calls.isEmpty)
    }

    @Test func keyCombosComeFromAClosedVocabulary() {
        #expect(ScreenShareDataChannel.decode(["t": "key", "combo": "cmd+c"]).isSuccess)
        #expect(ScreenShareDataChannel.decode(["t": "key", "combo": "cmd+v"]).isSuccess)
        #expect(ScreenShareDataChannel.decode(["t": "key", "combo": "ctrl+opt+shift+cmd+left"]).isSuccess)
        // No raw key codes, no unknown names, no doubled or trailing modifiers.
        #expect(ScreenShareDataChannel.decode(["t": "key", "combo": "cmd+f13"]).rejection == .unknownType)
        #expect(ScreenShareDataChannel.decode(["t": "key", "combo": "cmd+cmd+c"]).rejection == .unknownType)
        #expect(ScreenShareDataChannel.decode(["t": "key", "combo": "cmd+"]).rejection == .unknownType)
        #expect(ScreenShareDataChannel.decode(["t": "key", "combo": "CMD+C"]).rejection == .unknownType)
        #expect(ScreenShareDataChannel.decode(["t": "key", "code": 12]).rejection == .unknownType)
        #expect(ScreenShareDataChannel.decode(["t": "nonsense"]).rejection == .unknownType)
        #expect(ScreenShareDataChannel.decode(Data("not json".utf8)).rejection == .notJSON)
        // A single committed string stays small; a paste that size is the clipboard's job.
        #expect(ScreenShareDataChannel.decode(
            ["t": "text", "text": String(repeating: "a", count: 5_000)]).rejection == .textTooLong)
    }

    // MARK: Clipboard

    @Test func aChunkedClipboardIsReassembledWithinTheOneMegabyteCeiling() {
        var assembler = ScreenShareClipboardAssembler()
        let payload = Data(String(repeating: "x", count: 70_000).utf8)
        let frames = ScreenShareDataChannel.clipboardFrames(
            id: "t1", encoding: .raw, plaintextBytes: payload.count, payload: payload)
        // Every frame fits one data-channel message.
        for frame in frames {
            #expect(ScreenShareEngineTests.json(frame).count <= ScreenShareDataChannel.maximumMessageBytes)
        }
        #expect(frames.count == 3)
        var outcome: ScreenShareClipboardAssembler.Outcome = .waiting(received: 0, total: 0)
        for var frame in frames {
            // The Mac writes `to-phone`; read back as the phone's own upload.
            frame["dir"] = "to-mac"
            guard case .success(let parsed) = ScreenShareDataChannel.clipboardFrame(frame) else {
                Issue.record("a frame this code wrote did not parse"); return
            }
            outcome = assembler.accept(parsed)
        }
        guard case .complete(let encoding, let declared, let assembled) = outcome else {
            Issue.record("the transfer never completed"); return
        }
        #expect(encoding == .raw)
        #expect(declared == payload.count)
        #expect(assembled == payload)
        guard case .success(let text) = ScreenShareClipboardCodec.decode(
            encoding: encoding, declaredBytes: declared, payload: assembled, compressor: nil) else {
            Issue.record("the reassembled payload did not decode"); return
        }
        #expect(text.count == 70_000)
    }

    @Test func aClipboardTransferThatClaimsTooMuchIsRefusedBeforeAByteIsBuffered() {
        // The declared plaintext size is checked first, so a phone cannot make
        // the Mac hold a gigabyte by promising to send one.
        #expect(ScreenShareDataChannel.clipboardFrame([
            "t": "clipboard", "dir": "to-mac", "enc": "raw", "bytes": 50_000_000, "data": "AAAA",
            "id": "t1", "seq": 0, "total": 1,
        ]).rejection == .clipboardTooLarge)
        // A zstd payload that decompresses past the ceiling is refused too.
        let bomb = FakeCompressor(expanded: Data(repeating: 0x41, count: ScreenShareClipboardLimits.maximumBytes + 1))
        if case .failure(let rejection) = ScreenShareClipboardCodec.decode(
            encoding: .zstd, declaredBytes: 16, payload: Data([1, 2, 3]), compressor: bomb) {
            #expect(rejection == .malformed)
        } else { Issue.record("a decompression bomb was accepted") }
        // Without a compressor a zstd frame is refused rather than guessed at.
        if case .failure(let rejection) = ScreenShareClipboardCodec.decode(
            encoding: .zstd, declaredBytes: 4, payload: Data([1]), compressor: nil) {
            #expect(rejection == .malformed)
        } else { Issue.record("a zstd frame was read without a decompressor") }
    }

    @Test func theClipboardMovesBothWaysOnlyForAControlSession() async throws {
        let fixture = await makeFixture()
        defer { cleanUp(fixture) }
        let keystore = try #require(try await allow(fixture, grant: .control))
        guard case .success(let reply) = await startSession(fixture, mode: .control, keystore: keystore) else {
            Issue.record("control start refused"); return
        }
        let peer = try #require(await fixture.peers.peer(sessionId: reply.sessionId))

        // Phone → Mac.
        let payload = Data("붙여넣을 내용".utf8)
        for frame in ScreenShareDataChannel.clipboardFrames(
            id: "t1", encoding: .raw, plaintextBytes: payload.count, payload: payload) {
            var frame = frame
            frame["dir"] = "to-mac"
            await peer.emitData(Self.json(frame))
        }
        try await Task.sleep(nanoseconds: 60_000_000)
        #expect(fixture.pasteboard.written == "붙여넣을 내용")

        // Mac → phone, on the button only.
        fixture.pasteboard.set(text: "Mac의 클립보드")
        await peer.emitData(Self.json(["t": "clipboard-request"]))
        try await Task.sleep(nanoseconds: 60_000_000)
        let sent = await peer.sentData.compactMap {
            (try? JSONSerialization.jsonObject(with: $0)) as? [String: Any]
        }.filter { $0["t"] as? String == "clipboard" }
        #expect(!sent.isEmpty)
        #expect(sent.allSatisfy { $0["dir"] as? String == "to-phone" })
        let rebuilt = sent.sorted { ($0["seq"] as? Int ?? 0) < ($1["seq"] as? Int ?? 0) }
            .compactMap { Data(base64Encoded: $0["data"] as? String ?? "") }
            .reduce(Data(), +)
        #expect(String(data: rebuilt, encoding: .utf8) == "Mac의 클립보드")
    }

    @Test func aConcealedPasteboardItemIsNeverRead() async throws {
        let fixture = await makeFixture()
        defer { cleanUp(fixture) }
        let keystore = try #require(try await allow(fixture, grant: .control))
        guard case .success(let reply) = await startSession(fixture, mode: .control, keystore: keystore) else {
            Issue.record("control start refused"); return
        }
        let peer = try #require(await fixture.peers.peer(sessionId: reply.sessionId))
        fixture.pasteboard.setConcealed()
        await peer.emitData(Self.json(["t": "clipboard-request"]))
        try await Task.sleep(nanoseconds: 60_000_000)
        let sent = await peer.sentData.compactMap {
            (try? JSONSerialization.jsonObject(with: $0)) as? [String: Any]
        }.filter { $0["t"] as? String == "clipboard" }
        let frame = try #require(sent.first)
        #expect(frame["concealed"] as? Bool == true)
        // Nothing of the pasteboard itself left the Mac.
        #expect((frame["data"] as? String ?? "").isEmpty)
        #expect(frame["bytes"] as? Int == 0)
    }

    // MARK: Background and host stops

    @Test func thePhoneReportsTheBackgroundAndTheHostEndsTheSession() async throws {
        let fixture = await makeFixture(fast: [ScreenSharePolicy.backgroundTimeout])
        defer { cleanUp(fixture) }
        _ = try await allow(fixture, grant: .view)
        guard case .success(let reply) = await startSession(fixture, mode: .view) else {
            Issue.record("start refused"); return
        }
        // Connected, so only the background rule can end it.
        let peer = try #require(await fixture.peers.peer(sessionId: reply.sessionId))
        await peer.emitConnected(.host)
        await fixture.engine.handle(.background(sessionId: reply.sessionId, background: true), from: Self.phone)
        // The fixture's sleeper makes the 30 s rule a millisecond; the rule itself
        // is the host's, which is the point.
        #expect(await waitUntil { await fixture.signals.sent.contains { $0.type == "screen-session-end" } })
        #expect(await fixture.engine.liveSessionIds.isEmpty)
        #expect(ScreenSharePolicy.backgroundTimeout == 30)
        let end = await fixture.signals.sent.last { $0.type == "screen-session-end" }
        guard case .sessionEnd(_, let reason) = end else { Issue.record("no session end"); return }
        #expect(reason == "background")
    }

    @Test func aKillSwitchStopsEveryPeerAndTellsEveryPhone() async throws {
        let fixture = await makeFixture()
        defer { cleanUp(fixture) }
        _ = try await allow(fixture, grant: .view)
        _ = try await allow(fixture, deviceId: Self.viewer, grant: .view)
        guard case .success(let first) = await startSession(fixture, mode: .view),
              case .success(let second) = await startSession(fixture, deviceId: Self.viewer, mode: .view)
        else { Issue.record("start refused"); return }
        await fixture.signals.clear()

        let timing = await fixture.service.killSwitch()
        await fixture.engine.hostStopped([
            ScreenShareStoppedSession(sessionId: first.sessionId, deviceId: Self.phone, reason: .killSwitch),
            ScreenShareStoppedSession(sessionId: second.sessionId, deviceId: Self.viewer, reason: .killSwitch),
        ])
        #expect(timing.elapsed <= ScreenSharePolicy.killDeadline)
        #expect(await fixture.engine.liveSessionIds.isEmpty)
        let kills = await fixture.signals.sent.filter { $0.type == "screen-kill" }
        #expect(kills.count == 2)
        if case .kill(_, let reason) = kills[0] { #expect(reason == "kill-switch") }
        #expect(await fixture.peers.closedPeers() == 2)
    }

    @Test func revokingAPhoneStopsItsSessionAndTellsIt() async throws {
        let fixture = await makeFixture()
        defer { cleanUp(fixture) }
        _ = try await allow(fixture, grant: .view)
        guard case .success(let reply) = await startSession(fixture, mode: .view) else {
            Issue.record("start refused"); return
        }
        await fixture.signals.clear()
        // The engine is wired to the service the way the app wires it.
        await fixture.service.observeStops { [engine = fixture.engine] stopped in
            await engine.hostStopped(stopped)
        }
        await fixture.service.deviceRevoked(Self.phone)
        #expect(await fixture.engine.liveSessionIds.isEmpty)
        let kill = try #require(await fixture.signals.sent.first { $0.type == "screen-kill" })
        guard case .kill(let sessionId, let reason) = kill else { Issue.record("not a kill"); return }
        #expect(sessionId == reply.sessionId)
        #expect(reason == "revoked")
    }

    @Test func aLockScreenStopsTheSessionAndSaysWhy() async throws {
        let fixture = await makeFixture()
        defer { cleanUp(fixture) }
        _ = try await allow(fixture, grant: .view)
        guard case .success = await startSession(fixture, mode: .view) else {
            Issue.record("start refused"); return
        }
        await fixture.signals.clear()
        await fixture.service.observeFrameBlock { [engine = fixture.engine] blocked, reason in
            await engine.framesBlockedChanged(blocked, reason: reason)
        }
        fixture.environment.locked = true
        await fixture.service.refreshEnvironment()
        #expect(await fixture.engine.liveSessionIds.isEmpty)
        let kill = try #require(await fixture.signals.sent.first { $0.type == "screen-kill" })
        guard case .kill(_, let reason) = kill else { Issue.record("not a kill"); return }
        #expect(reason == "lock-screen")
    }

    @Test func theSessionLogRecordsTheDeviceAndModeAndNeverAKeystroke() async throws {
        let fixture = await makeFixture()
        defer { cleanUp(fixture) }
        let keystore = try #require(try await allow(fixture, grant: .control))
        guard case .success(let reply) = await startSession(fixture, mode: .control, keystore: keystore) else {
            Issue.record("control start refused"); return
        }
        let peer = try #require(await fixture.peers.peer(sessionId: reply.sessionId))
        await peer.emitData(Self.json(["t": "text", "text": "비밀 문장"]))
        try await Task.sleep(nanoseconds: 40_000_000)
        await fixture.engine.end(sessionId: reply.sessionId, reason: .peerLeft)
        let log = await fixture.service.sessionLog()
        let entry = try #require(log.first)
        #expect(entry.deviceId == Self.phone)
        #expect(entry.mode == .control)
        #expect(entry.endedAt != nil)
        // The whole log is device, mode and timestamps; there is nowhere for a
        // keystroke to be, and this is the assertion that keeps it that way.
        #expect(!String(describing: log).contains("비밀 문장"))
    }

    // MARK: Helpers

    static func json(_ object: [String: Any]) -> Data {
        (try? JSONSerialization.data(withJSONObject: object, options: [.sortedKeys])) ?? Data()
    }
}

// MARK: - Session flow: ordering, refusals, deadlines, adaptation

extension ScreenShareEngineTests {
    @Test func theFirstOfferAndItsCandidatesWaitForTheRouteReply() async throws {
        let fixture = await makeFixture()
        defer { cleanUp(fixture) }
        _ = try await allow(fixture, grant: .view)
        await fixture.signals.clear()
        guard case .success(let reply) = await startSession(fixture, mode: .view, sendOffer: false) else {
            Issue.record("start refused"); return
        }
        // Candidates gathered while the reply is still on its way are held: the
        // phone does not know this session id yet.
        let peer = try #require(await fixture.peers.peer(sessionId: reply.sessionId))
        await peer.emitLocalCandidate("candidate:1 host")
        await peer.emitLocalCandidate("candidate:2 srflx")
        #expect(await fixture.signals.sent.allSatisfy { $0.type == "screen-grant" })

        await fixture.engine.sendInitialOffer(sessionId: reply.sessionId)
        let frames = await fixture.signals.sent.filter { $0.type != "screen-grant" }
        #expect(frames.map(\.type) == ["screen-offer", "screen-ice", "screen-ice"])
        guard case .ice(_, let first, _, _, _) = frames[1], case .ice(_, let second, _, _, _) = frames[2] else {
            Issue.record("candidates out of shape"); return
        }
        #expect([first, second] == ["candidate:1 host", "candidate:2 srflx"])
        // Sent once only.
        await fixture.engine.sendInitialOffer(sessionId: reply.sessionId)
        #expect(await fixture.signals.sent.filter { $0.type == "screen-offer" }.count == 1)
        // From now on a candidate goes straight out.
        await peer.emitLocalCandidate("candidate:3 relay")
        #expect(await fixture.signals.sent.last?.type == "screen-ice")
    }

    @Test func aLockedMacRefusesTheStartAndNeverStartsCapture() async throws {
        let fixture = await makeFixture()
        defer { cleanUp(fixture) }
        _ = try await allow(fixture, grant: .view)
        fixture.environment.locked = true
        let started = await startSession(fixture, mode: .view)
        #expect(started.refusal == .lockScreen)
        #expect(await fixture.capture.plans.isEmpty)
        #expect(await fixture.engine.liveSessionIds.isEmpty)
        #expect(await fixture.service.liveSessions().isEmpty)
        #expect(await fixture.peers.closedPeers() == 1)
    }

    @Test func aPasswordFieldRefusesTheStartWithItsOwnReason() async throws {
        let fixture = await makeFixture()
        defer { cleanUp(fixture) }
        _ = try await allow(fixture, grant: .view)
        fixture.environment.secureInput = true
        #expect(await startSession(fixture, mode: .view).refusal == .secureInput)
        #expect(await fixture.capture.plans.isEmpty)
    }

    @Test func aLockDuringTheJoinsCaptureStartStopsTheLateCapture() async throws {
        let fixture = await makeFixture()
        defer { cleanUp(fixture) }
        _ = try await allow(fixture, grant: .view)
        // The lock screen comes on while ScreenCaptureKit is still starting.
        await fixture.capture.holdApply()
        let start = Task { await startSession(fixture, mode: .view) }
        #expect(await waitUntil { await fixture.capture.isHolding })
        fixture.environment.locked = true
        await fixture.service.refreshEnvironment()
        await fixture.capture.releaseApply()
        let result = await start.value
        #expect(result.refusal == .lockScreen)
        // The capture that came up after the lock was stopped again, and no
        // session is left holding it.
        #expect(await fixture.capture.stops >= 1)
        #expect(await fixture.service.liveSessions().isEmpty)
        #expect(await fixture.engine.liveSessionIds.isEmpty)
    }

    @Test func aMissingScreenRecordingGrantIsTheScreenPermissionRefusal() async throws {
        let fixture = await makeFixture()
        defer { cleanUp(fixture) }
        _ = try await allow(fixture, grant: .view)
        await fixture.capture.setFailure(.permissionDenied)
        #expect(await startSession(fixture, mode: .view).refusal == .screenPermission)
        #expect(await fixture.service.liveSessions().isEmpty)
        #expect(await fixture.engine.liveSessionIds.isEmpty)
    }

    @Test func aPeerThatNeverConnectsIsGivenUp() async throws {
        let fixture = await makeFixture(fast: [ScreenShareEngine.connectDeadline])
        defer { cleanUp(fixture) }
        _ = try await allow(fixture, grant: .view)
        _ = try await allow(fixture, deviceId: Self.viewer, grant: .view)
        guard case .success(let lost) = await startSession(fixture, mode: .view),
              case .success(let kept) = await startSession(fixture, deviceId: Self.viewer, mode: .view)
        else { Issue.record("start refused"); return }
        await fixture.peers.peer(sessionId: kept.sessionId)?.emitConnected(.host)
        #expect(await waitUntil { await fixture.peers.peer(sessionId: lost.sessionId)?.isClosed == true })
        #expect(await fixture.engine.liveSessionIds == [kept.sessionId])
    }

    @Test func aRelayedPathCapsTheBitrateToTheTurnQuotaWithoutRenegotiating() async throws {
        let fixture = await makeFixture()
        defer { cleanUp(fixture) }
        _ = try await allow(fixture, grant: .view)
        guard case .success(let reply) = await startSession(fixture, mode: .view) else {
            Issue.record("start refused"); return
        }
        let peer = try #require(await fixture.peers.peer(sessionId: reply.sessionId))
        let offers = await peer.offerLog.count
        await peer.emitConnected(.relay)
        #expect(await peer.qualities.last?.maxBitrateKbps == ScreenShareQuality.turnSessionQuotaKbps)
        #expect(await peer.offerLog.count == offers)
        // Same Wi-Fi keeps the full ceiling.
        guard case .success(let other) = await startSession(fixture, mode: .view) else {
            Issue.record("start refused"); return
        }
        let direct = try #require(await fixture.peers.peer(sessionId: other.sessionId))
        await direct.emitConnected(.host)
        #expect(await direct.qualities.isEmpty)
    }

    @Test func aSoftwareCodecFallsBackToH264WhenTheMacRunsHot() async throws {
        let load = MutableLoad(headroom: 0.9, thermal: false)
        let fixture = await makeFixture(load: load)
        defer { cleanUp(fixture) }
        _ = try await allow(fixture, grant: .view)
        guard case .success(let reply) = await startSession(
            fixture, mode: .view, network: "cellular", decodes: ["H264", "VP9"]) else {
            Issue.record("start refused"); return
        }
        #expect(reply.codec == "VP9")
        // Still cool: nothing changes, and the watch keeps going.
        #expect(await fixture.engine.checkMachineLoad())
        load.thermal = true
        #expect(await fixture.engine.checkMachineLoad() == false)
        #expect(await fixture.engine.session(reply.sessionId)?.codec == .h264)
        let offer = await fixture.signals.sent.last { $0.type == "screen-offer" }
        guard case .offer(_, _, _, _, let codec, _, let iceRestart) = offer else { Issue.record("no offer"); return }
        #expect(codec == .h264)
        #expect(iceRestart == false)
    }

    @Test func aRenewalReusesACredentialAnotherSessionJustMintedAndCarriesTheRealGrant() async throws {
        let fixture = await makeFixture()
        defer { cleanUp(fixture) }
        let keystore = try #require(try await allow(fixture, grant: .control))
        _ = try await allow(fixture, deviceId: Self.viewer, grant: .view)
        // A phone with control watching in view mode still holds control.
        guard case .success(let watching) = await startSession(fixture, mode: .view),
              case .success(let viewer) = await startSession(fixture, deviceId: Self.viewer, mode: .view)
        else { Issue.record("start refused"); return }
        _ = keystore
        await fixture.signals.clear()
        await fixture.engine.renewCredential(sessionId: watching.sessionId)
        await fixture.engine.renewCredential(sessionId: viewer.sessionId)
        let grants = await fixture.signals.sent.compactMap { signal -> (ScreenShareGrant, String?, [ScreenShareIceServer]?)? in
            guard case .grant(_, _, let grant, let challenge, let servers, _) = signal else { return nil }
            return (grant, challenge, servers)
        }
        #expect(grants.count == 2)
        #expect(grants[0].0 == .control)
        #expect(grants[0].1 != nil)
        #expect(grants[1].0 == .view)
        #expect(grants[1].1 == nil)
        // One mint served both renewals.
        let credentials = Set(grants.compactMap { $0.2?.first { $0.username != nil }?.credential })
        #expect(credentials.count == 1)
    }

    @Test func aViewerCannotPullTheControllersScreenAway() async throws {
        let fixture = await makeFixture()
        defer { cleanUp(fixture) }
        let keystore = try #require(try await allow(fixture, grant: .control))
        _ = try await allow(fixture, deviceId: Self.viewer, grant: .view)
        guard case .success = await startSession(fixture, mode: .control, keystore: keystore),
              case .success(let viewer) = await startSession(fixture, deviceId: Self.viewer, mode: .view)
        else { Issue.record("start refused"); return }
        let plans = await fixture.capture.plans.count
        let peer = try #require(await fixture.peers.peer(sessionId: viewer.sessionId))
        await peer.emitData(Self.json(["t": "display", "displayId": 2]))
        await peer.emitData(Self.json(["t": "zoom", "displayId": 1,
                                       "region": ["x": 0.1, "y": 0.1, "width": 0.2, "height": 0.2]]))
        #expect(await fixture.capture.plans.count == plans)
    }

    @Test func theControllersDisplaySwitchRestartsIceForEveryPhone() async throws {
        let fixture = await makeFixture()
        defer { cleanUp(fixture) }
        let keystore = try #require(try await allow(fixture, grant: .control))
        _ = try await allow(fixture, deviceId: Self.viewer, grant: .view)
        guard case .success(let control) = await startSession(fixture, mode: .control, keystore: keystore),
              case .success(let viewer) = await startSession(fixture, deviceId: Self.viewer, mode: .view)
        else { Issue.record("start refused"); return }
        await fixture.signals.clear()
        let peer = try #require(await fixture.peers.peer(sessionId: control.sessionId))
        await peer.emitData(Self.json(["t": "display", "displayId": 2]))
        #expect(await fixture.capture.lastPlan?.displayId == 2)
        let offers = await fixture.signals.sent.compactMap { signal -> (String, UInt32, Bool)? in
            guard case .offer(let id, _, _, let display, _, _, let restart) = signal else { return nil }
            return (id, display, restart)
        }
        #expect(Set(offers.map(\.0)) == [control.sessionId, viewer.sessionId])
        #expect(offers.allSatisfy { $0.1 == 2 && $0.2 })
    }

    @Test func aDisplayThatVanishesWithNothingToFallBackToEndsEverySession() async throws {
        let fixture = await makeFixture(layout: [1: CGRect(x: 0, y: 0, width: 1920, height: 1080)])
        defer { cleanUp(fixture) }
        _ = try await allow(fixture, grant: .view)
        guard case .success(let reply) = await startSession(fixture, mode: .view) else {
            Issue.record("start refused"); return
        }
        fixture.displays.unplug(1)
        await fixture.engine.captureInterrupted()
        #expect(await fixture.engine.liveSessionIds.isEmpty)
        let end = await fixture.signals.sent.last { $0.type == "screen-session-end" }
        guard case .sessionEnd(let id, let reason) = end else { Issue.record("no end"); return }
        #expect(id == reply.sessionId)
        #expect(reason == "display-gone")
    }

    @Test func framesGoOutPerLayerAndAStillScreenIsHandedToANewlyConnectedPhone() async throws {
        let fixture = await makeFixture()
        defer { cleanUp(fixture) }
        _ = try await allow(fixture, grant: .view)
        guard case .success(let reply) = await startSession(fixture, mode: .view) else {
            Issue.record("start refused"); return
        }
        let peer = try #require(await fixture.peers.peer(sessionId: reply.sessionId))
        let frame = try #require(Self.frame(.primary))
        await fixture.engine.deliver(frame: frame, status: .complete, dirtyRects: 1)
        // Unchanged pixels are not encoded again.
        await fixture.engine.deliver(frame: frame, status: .complete, dirtyRects: 0)
        await fixture.engine.deliver(frame: frame, status: .idle, dirtyRects: 0)
        // Not zoomed: an overview frame has no layer to go to.
        await fixture.engine.deliver(frame: try #require(Self.frame(.overview)), status: .complete, dirtyRects: 1)
        #expect(await peer.sentLayers == [.primary])
        // The phone connects after the screen went still: it is handed the
        // picture the encoder already has instead of waiting for a change.
        await peer.emitConnected(.host)
        #expect(await peer.sentLayers == [.primary, .primary])
        // Capture stopped (a kill, the lock screen): a frame in flight is dropped.
        _ = await fixture.service.killSwitch()
        await fixture.engine.deliver(frame: frame, status: .complete, dirtyRects: 1)
        #expect(await peer.sentLayers.count == 2)
    }

    @Test func anIdleTimeoutReachesThePhoneAsIdleTimeout() async throws {
        let clock = ScreenShareTestClock(Date(timeIntervalSinceReferenceDate: 0))
        let started = ScreenShareFlag()
        // Ten idle minutes pass once the session is fully up.
        let host = ScreenShareHost(now: { clock.date }, idleSleep: { _ in
            while !started.isSet { try await Task.sleep(nanoseconds: 1_000_000) }
            clock.advance(by: 601)
        })
        let fixture = await makeFixture(host: host)
        defer { cleanUp(fixture) }
        let keystore = try #require(try await allow(fixture, grant: .control))
        guard case .success(let reply) = await startSession(fixture, mode: .control, keystore: keystore) else {
            Issue.record("start refused"); return
        }
        started.set()
        #expect(await waitUntil { await fixture.signals.sent.contains { $0.type == "screen-session-end" } })
        #expect(await fixture.engine.liveSessionIds.isEmpty)
        let end = await fixture.signals.sent.last { $0.type == "screen-session-end" }
        guard case .sessionEnd(let id, let reason) = end else { Issue.record("no end"); return }
        #expect(id == reply.sessionId)
        #expect(reason == "idle-timeout")
    }

    @Test func aSecondEnrolmentWhileTheDialogIsOpenIsTurnedAway() async throws {
        let confirmer = HeldConfirmer()
        let fixture = await makeFixture(confirmer: confirmer)
        defer { cleanUp(fixture) }
        try await fixture.service.setAllowed(deviceId: Self.phone, allowed: true)
        try await fixture.service.setGrant(deviceId: Self.phone, grant: .control)
        let key = Keystore().publicKeyData.base64EncodedString()
        let first = Task { await fixture.engine.enrolControlKey(deviceId: Self.phone, publicKeyB64: key) }
        #expect(await waitUntil { await confirmer.isAsking })
        let second = await fixture.engine.enrolControlKey(deviceId: Self.phone, publicKeyB64: key)
        #expect(second.refusal == .controlKeyPending)
        await confirmer.answer(true)
        #expect(await first.value.isSuccess)
        #expect(await confirmer.asked == 1)
    }

    // MARK: Helpers

    static func frame(_ layer: ScreenShareCaptureLayer.Kind) -> ScreenShareVideoFrame? {
        var buffer: CVPixelBuffer?
        CVPixelBufferCreate(nil, 4, 4, kCVPixelFormatType_420YpCbCr8BiPlanarFullRange, nil, &buffer)
        guard let buffer else { return nil }
        return ScreenShareVideoFrame(pixelBuffer: buffer, layer: layer, timestampNanos: 1)
    }

    func waitUntil(timeout: TimeInterval = 5, _ condition: @Sendable () async -> Bool) async -> Bool {
        let deadline = Date().addingTimeInterval(timeout)
        while Date() < deadline {
            if await condition() { return true }
            try? await Task.sleep(nanoseconds: 2_000_000)
        }
        return await condition()
    }
}

final class ScreenShareFlag: @unchecked Sendable {
    private let lock = NSLock()
    private var value = false
    var isSet: Bool { lock.lock(); defer { lock.unlock() }; return value }
    func set() { lock.lock(); value = true; lock.unlock() }
}

final class MutableLoad: ScreenShareMachineLoad, @unchecked Sendable {
    private let lock = NSLock()
    private var headroomValue: Double
    private var thermalValue: Bool
    init(headroom: Double, thermal: Bool) { headroomValue = headroom; thermalValue = thermal }
    var thermal: Bool {
        get { lock.lock(); defer { lock.unlock() }; return thermalValue }
        set { lock.lock(); thermalValue = newValue; lock.unlock() }
    }
    func cpuHeadroom() -> Double { lock.lock(); defer { lock.unlock() }; return headroomValue }
    func thermalPressure() -> Bool { thermal }
}

/// A confirmation dialog the test answers by hand.
actor HeldConfirmer: ScreenShareControlKeyConfirmer {
    private var waiter: CheckedContinuation<Bool, Never>?
    private(set) var asked = 0
    var isAsking: Bool { waiter != nil }

    func confirmControlKey(deviceId: String, fingerprint: String) async -> Bool {
        asked += 1
        return await withCheckedContinuation { waiter = $0 }
    }

    func answer(_ value: Bool) {
        waiter?.resume(returning: value)
        waiter = nil
    }
}

// MARK: - Routes over the m1 tunnel

extension ScreenShareEngineTests {
    private func route(_ service: MobileRemoteService, _ method: String, _ path: String,
                       _ body: [String: Any]? = nil, device: String = phone) async -> (MobileReply, [String: Any]) {
        let data = body.flatMap { try? JSONSerialization.data(withJSONObject: $0) }
        let reply = await service.route(method: method, path: path, body: data, deviceId: device)
        return (reply, (try? JSONSerialization.jsonObject(with: reply.body)) as? [String: Any] ?? [:])
    }

    @Test func screenShareIsAdvertisedAndRoutedOnlyOnceTheEngineIsAttached() async throws {
        let fixture = await makeFixture()
        defer { cleanUp(fixture) }
        let remote = MobileRemoteService(dataDirectory: fixture.directory.appendingPathComponent("remote"),
                                         hostName: "Route Mac", watchesNetwork: false)
        // The service holds its delegate weakly; the test keeps it alive.
        let host = RouteHost()
        await remote.attach(host)
        defer { withExtendedLifetime(host) {} }

        let (_, before) = await route(remote, "GET", "/m1/info")
        #expect((before["capabilities"] as? [String])?.contains("screenShare") == false)
        #expect(await route(remote, "GET", "/m1/screen-share/state").0.status == 503)

        await remote.attachScreenShareEngine(fixture.engine)
        let (_, after) = await route(remote, "GET", "/m1/info")
        #expect((after["capabilities"] as? [String])?.contains("screenShare") == true)
        #expect(await remote.capabilities.contains("screenShare"))

        // State for a phone the Mac has not allowed: answered, and empty.
        let (stateReply, state) = await route(remote, "GET", "/m1/screen-share/state")
        #expect(stateReply.status == 200)
        let body = try #require(state["screenShare"] as? [String: Any])
        #expect(body["allowed"] as? Bool == false)
        #expect(body["grant"] as? String == "none")
        #expect(body["isBeta"] as? Bool == true)

        // A refusal carries the documented reason.
        let (refused, refusal) = await route(remote, "POST", "/m1/screen-share/sessions", ["mode": "view"])
        #expect(refused.status == 403)
        #expect((refusal["error"] as? [String: Any])?["reason"] as? String == "device-not-allowed")
        let (legacy, legacyBody) = await route(remote, "GET", "/m1/screen-share/state",
                                               device: MobileDeviceRegistry.legacyId)
        #expect(legacy.status == 403)
        #expect((legacyBody["error"] as? [String: Any])?["reason"] as? String == "legacy-client")
        #expect(await route(remote, "GET", "/m1/screen-share/nothing").0.status == 404)
        #expect(await route(remote, "POST", "/m1/screen-share/control-key",
                            ["publicKeyB64": String(repeating: "A", count: 400)]).0.status == 400)
    }

    @Test func theSessionRouteRepliesFirstAndOnlyThenSendsTheOffer() async throws {
        let fixture = await makeFixture()
        defer { cleanUp(fixture) }
        let remote = MobileRemoteService(dataDirectory: fixture.directory.appendingPathComponent("remote"),
                                         hostName: "Route Mac", watchesNetwork: false)
        // The service holds its delegate weakly; the test keeps it alive.
        let host = RouteHost()
        await remote.attach(host)
        defer { withExtendedLifetime(host) {} }
        await remote.attachScreenShareEngine(fixture.engine)
        _ = try await allow(fixture, grant: .view)
        await fixture.signals.clear()

        let (reply, body) = await route(remote, "POST", "/m1/screen-share/sessions",
                                        ["mode": "view", "displayId": 1, "network": "wifi", "decodes": ["H264"]])
        #expect(reply.status == 200)
        let sessionId = try #require(body["sessionId"] as? String)
        #expect(body["codec"] as? String == "H264")
        #expect(await fixture.signals.sent.filter { $0.type == "screen-offer" }.isEmpty)
        let afterReply = try #require(reply.afterReply)
        await afterReply()
        let offer = try #require(await fixture.signals.sent.first { $0.type == "screen-offer" })
        #expect(offer.sessionId == sessionId)
    }

    @Test func theControlKeyRouteEnrolsOnceTheMacUserConfirms() async throws {
        let fixture = await makeFixture()
        defer { cleanUp(fixture) }
        let remote = MobileRemoteService(dataDirectory: fixture.directory.appendingPathComponent("remote"),
                                         hostName: "Route Mac", watchesNetwork: false)
        // The service holds its delegate weakly; the test keeps it alive.
        let host = RouteHost()
        await remote.attach(host)
        defer { withExtendedLifetime(host) {} }
        await remote.attachScreenShareEngine(fixture.engine)
        try await fixture.service.setAllowed(deviceId: Self.phone, allowed: true)
        try await fixture.service.setGrant(deviceId: Self.phone, grant: .control)
        let key = Keystore().publicKeyData
        let (reply, body) = await route(remote, "POST", "/m1/screen-share/control-key",
                                        ["publicKeyB64": key.base64EncodedString()])
        #expect(reply.status == 200)
        #expect(body["fingerprint"] as? String == ScreenShareControlKey.fingerprint(key))
        #expect(await fixture.confirmer.fingerprints == [ScreenShareControlKey.fingerprint(key)])
        // The state now says a key is enrolled, by fingerprint only.
        let (_, state) = await route(remote, "GET", "/m1/screen-share/state")
        let screen = try #require(state["screenShare"] as? [String: Any])
        #expect(screen["controlKeyFingerprint"] as? String == ScreenShareControlKey.fingerprint(key))
        // Another key is a conflict, never a replacement.
        let (conflict, conflictBody) = await route(remote, "POST", "/m1/screen-share/control-key",
                                                   ["publicKeyB64": Keystore().publicKeyData.base64EncodedString()])
        #expect(conflict.status == 409)
        #expect((conflictBody["error"] as? [String: Any])?["reason"] as? String == "control-key-present")
    }
}

/// The least a Mac needs for the m1 tunnel to route at all.
private final class RouteHost: MobileHostDelegate, @unchecked Sendable {
    func mobileState() async -> MobileState { MobileState(revision: 1, hostName: "Route Mac", workspaces: [], sessions: []) }
    func mobileSession(id: String) async -> MobileSessionDetail? { nil }
    func mobileSubmit(sessionId: String, text: String, mode: String?, attachments: [RunAttachment]) async throws -> String { "started" }
    func mobileGuided(sessionId: String, style: String, skill: String, text: String) async throws -> String { "started" }
    func mobileStop(sessionId: String) async throws -> Bool { false }
    func mobilePermission(sessionId: String, requestId: String, runId: String, allow: Bool) async throws {}
    func mobileAnswers(sessionId: String, requestId: String, runId: String, answers: [String: UserQuestionAnswer]) async throws {}
    func mobileCreateSession(workspaceId: String, kind: String, provider: String) async throws -> String { "new" }
    func mobileRemoveQueued(sessionId: String, itemId: String) async throws {}
    func mobileRunNextQueued(sessionId: String) async throws {}
    func mobileRename(sessionId: String, title: String, titleMode: String?) async throws {}
    func mobileClose(sessionId: String) async throws {}
    func mobileEntries(sessionId: String, before: String, limit: Int) async throws -> MobileEntriesPage { MobileEntriesPage(entries: [], hasMore: false) }
    func mobileApplySettings(sessionId: String, request: MobileSettingsRequest) async throws {}
    func mobileCommands(sessionId: String) async throws -> [MobileCommand] { [] }
    func mobilePerformCommand(sessionId: String, action: String) async throws -> String? { nil }
}

// MARK: - Fakes

/// A peer connection with no network behind it: it records what the engine asked
/// for and lets a test play the phone's side back.
actor FakeScreenSharePeer: ScreenSharePeerConnection {
    struct OfferRecord: Equatable {
        var codec: ScreenShareVideoCodec
        var quality: ScreenShareQualityProfile
        var iceRestart: Bool
    }

    private(set) var offerLog: [OfferRecord] = []
    private(set) var configuredServers: [[ScreenShareIceServer]] = []
    private(set) var answer: String?
    private(set) var remoteCandidates: [String] = []
    private(set) var sentData: [Data] = []
    private(set) var frames = 0
    private(set) var isClosed = false
    /// True when every `setConfiguration` arrived before the ICE restart that
    /// follows it, which is the order the renewal has to keep.
    private(set) var configurationBeforeRestart = true
    private var events: ScreenSharePeerEvents?

    func attach(_ events: ScreenSharePeerEvents) { self.events = events }

    func setConfiguration(iceServers: [ScreenShareIceServer]) async {
        configuredServers.append(iceServers)
    }

    func createOffer(codec: ScreenShareVideoCodec, quality: ScreenShareQualityProfile,
                     iceRestart: Bool) async throws -> String {
        if iceRestart, configuredServers.isEmpty { configurationBeforeRestart = false }
        offerLog.append(OfferRecord(codec: codec, quality: quality, iceRestart: iceRestart))
        return "v=0\r\no=- \(offerLog.count) 0 IN IP4 127.0.0.1\r\n"
    }

    func acceptAnswer(_ sdp: String) async throws { answer = sdp }

    private(set) var qualities: [ScreenShareQualityProfile] = []
    func setQuality(_ quality: ScreenShareQualityProfile) async { qualities.append(quality) }

    func addRemoteCandidate(candidate: String, sdpMid: String?, sdpMLineIndex: Int?) async {
        remoteCandidates.append(candidate)
    }

    private(set) var sentLayers: [ScreenShareCaptureLayer.Kind] = []
    func send(frame: ScreenShareVideoFrame) async { frames += 1; sentLayers.append(frame.layer) }

    func sendData(_ data: Data) async -> Bool {
        guard !isClosed else { return false }
        sentData.append(data)
        return true
    }

    func close() async { isClosed = true }

    func statistics() async -> ScreenSharePeerStats { ScreenSharePeerStats(selectedPath: .host) }

    // The phone's side, driven by the test.
    func emitLocalCandidate(_ candidate: String) async {
        await events?.localCandidate(candidate, "0", 0, nil)
    }
    func emitData(_ data: Data) async { await events?.data(data) }
    func emitConnected(_ path: ScreenShareIcePath?) async { await events?.connected(path) }
    func emitFailed() async { await events?.failed() }
}

actor FakePeerFactory: ScreenSharePeerFactory {
    private var made: [String: FakeScreenSharePeer] = [:]

    func makePeer(sessionId: String, iceServers: [ScreenShareIceServer],
                  events: ScreenSharePeerEvents) async throws -> ScreenSharePeerConnection {
        let peer = FakeScreenSharePeer()
        await peer.attach(events)
        made[sessionId] = peer
        return peer
    }

    func peer(sessionId: String) -> FakeScreenSharePeer? { made[sessionId] }

    /// How many of the peers handed out have been closed.
    func closedPeers() async -> Int {
        var count = 0
        for peer in made.values where await peer.isClosed { count += 1 }
        return count
    }
}

/// Records every signalling frame the engine sent, in order.
actor RecordingSignalSender: ScreenShareSignalSender {
    private(set) var sent: [ScreenShareSignal] = []
    private(set) var targets: [String] = []

    @discardableResult
    func send(_ signal: ScreenShareSignal, to deviceId: String) async -> Bool {
        // The frame has to survive the contract's own limits to count as sent.
        guard signal.encoded() != nil else { return false }
        sent.append(signal)
        targets.append(deviceId)
        return true
    }

    func clear() { sent = []; targets = [] }
}

/// The relay's TURN minting, with a fresh credential each time so a renewal is
/// visibly different from the pair it replaced.
actor FakeTurnSource: ScreenShareTurnSource {
    private var issued = 0
    private var unavailable = false

    func setUnavailable(_ value: Bool) { unavailable = value }

    func mintTurnCredential() async -> ScreenShareTurnCredential? {
        guard !unavailable else { return nil }
        issued += 1
        return ScreenShareTurnCredential(
            username: "\(issued):abcd", password: "secret-hmac-\(issued)",
            uris: ["turn:relay.example:3478?transport=udp"], ttl: 3600, issuedAt: Date())
    }
}

actor RecordingCaptureBackend: ScreenShareCaptureBackend {
    private(set) var plans: [ScreenShareCapturePlan] = []
    private(set) var stops = 0
    private var failure: ScreenShareCaptureError?
    private var holding = false
    private var held: CheckedContinuation<Void, Never>?

    func setFailure(_ value: ScreenShareCaptureError?) { failure = value }
    /// The next `apply` waits for `releaseApply`, like a ScreenCaptureKit start
    /// that is still coming up.
    func holdApply() { holding = true }
    var isHolding: Bool { held != nil }
    func releaseApply() { holding = false; held?.resume(); held = nil }

    func apply(_ plan: ScreenShareCapturePlan) async throws {
        if let failure { throw failure }
        if holding { await withCheckedContinuation { held = $0 } }
        plans.append(plan)
    }

    func stop() async { stops += 1 }

    var lastPlan: ScreenShareCapturePlan? { plans.last }
}

/// The Mac pasteboard, as a test decides it. A lock rather than an actor, because
/// the protocol is synchronous: `NSPasteboard` is.
/// Records where the engine asked for the latency marker.
final class RecordingTapMarker: ScreenShareTapMarkerSurface, @unchecked Sendable {
    private let lock = NSLock()
    private var recorded: [String] = []
    var shown: [String] { lock.lock(); defer { lock.unlock() }; return recorded }

    private func add(_ line: String) { lock.lock(); recorded.append(line); lock.unlock() }

    func showMarker(at position: CGPoint, displayId: UInt32) async -> Bool {
        add("\(position.x),\(position.y) display \(displayId)")
        return true
    }
}

final class FakePasteboard: ScreenSharePasteboard, @unchecked Sendable {
    private let lock = NSLock()
    private var value: ScreenSharePasteboardRead = .empty
    private var writtenValue: String?

    var written: String? { lock.lock(); defer { lock.unlock() }; return writtenValue }

    func set(text: String) {
        lock.lock(); value = ScreenSharePasteboardRead(text: text); lock.unlock()
    }
    /// A pasteboard carrying a type a password manager marked concealed.
    func setConcealed() {
        lock.lock(); value = ScreenSharePasteboardRead(text: nil, concealed: true); lock.unlock()
    }

    func read() -> ScreenSharePasteboardRead { lock.lock(); defer { lock.unlock() }; return value }
    func write(_ text: String) { lock.lock(); writtenValue = text; lock.unlock() }
}

actor FakeConfirmer: ScreenShareControlKeyConfirmer {
    private let answer: Bool
    private(set) var asked = 0
    private(set) var fingerprints: [String] = []

    init(answer: Bool) { self.answer = answer }

    func confirmControlKey(deviceId: String, fingerprint: String) async -> Bool {
        asked += 1
        fingerprints.append(fingerprint)
        return answer
    }
}

struct FakeLoad: ScreenShareMachineLoad {
    let headroom: Double
    let thermal: Bool
    func cpuHeadroom() -> Double { headroom }
    func thermalPressure() -> Bool { thermal }
}

/// A "compressor" whose decompression returns whatever the test handed it, so a
/// decompression bomb can be exercised without a real zstd stream.
struct FakeCompressor: ScreenShareCompressor {
    let expanded: Data
    func compress(_ data: Data) -> Data? { data }
    func decompress(_ data: Data, limit: Int) -> Data? { expanded.count > limit ? nil : expanded }
}

// MARK: - Result helpers

private extension Result where Failure == ScreenShareRouteRefusal {
    var isSuccess: Bool { if case .success = self { return true }; return false }
    var refusal: ScreenShareRouteRefusal? { if case .failure(let value) = self { return value }; return nil }
}

private extension Result where Success == ScreenShareDataMessage, Failure == ScreenShareDataRejection {
    var isSuccess: Bool { if case .success = self { return true }; return false }
    var rejection: ScreenShareDataRejection? { if case .failure(let value) = self { return value }; return nil }
}

private extension Result where Success == ScreenShareClipboardFrame, Failure == ScreenShareDataRejection {
    var rejection: ScreenShareDataRejection? { if case .failure(let value) = self { return value }; return nil }
}
