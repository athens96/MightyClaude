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
        stills: ScreenShareStillEncoder? = nil
    ) -> Fixture {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("screen-engine-" + UUID().uuidString, isDirectory: true)
        let store = ScreenShareSettingsStore(url: directory.appendingPathComponent("screen-share.json"))
        let displays = FakeDisplays(layout: layout, main: main)
        let input = FakeInput()
        let environment = FakeEnvironment()
        let service = ScreenShareService(store: store, displays: displays, input: input,
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
            displays: displays, pasteboard: pasteboard, confirmer: confirmer, load: load,
            compressor: nil, stills: stills,
            sleeper: { _ in try await Task.sleep(nanoseconds: 1_000_000) })
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
        network: String = "wifi", decodes: [String] = ["H264"]
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
        return await fixture.engine.start(deviceId: deviceId, request: request)
    }

    private func decodeRequest(_ object: [String: Any]) -> ScreenShareSessionRequestBody {
        var object = object
        if (object["controlSignatureB64"] as? String)?.isEmpty == true { object["controlSignatureB64"] = nil }
        let data = try! JSONSerialization.data(withJSONObject: object)
        return try! JSONDecoder().decode(ScreenShareSessionRequestBody.self, from: data)
    }

    // MARK: Capability

    @Test func hostAdvertisesScreenShare() {
        #expect(MobileCapability.all.contains("screenShare"))
    }

    // MARK: Routes

    @Test func stateHidesEverythingFromAPhoneTheMacHasNotAllowed() async throws {
        let fixture = makeFixture()
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
        let fixture = makeFixture()
        defer { cleanUp(fixture) }
        if case .failure(let refusal) = await fixture.engine.state(deviceId: MobileDeviceRegistry.legacyId) {
            #expect(refusal == .legacyClient)
        } else { Issue.record("a legacy phone was given screen-share state") }
        let started = await startSession(fixture, deviceId: MobileDeviceRegistry.legacyId, mode: .view)
        #expect(started.refusal == .legacyClient)
    }

    @Test func stateCarriesTheChallengeAndTheRelayMintedTurnCredentialOnceControlIsGranted() async throws {
        let fixture = makeFixture()
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
        let fixture = makeFixture()
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
        let fixture = makeFixture()
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
        let fixture = makeFixture()
        defer { cleanUp(fixture) }
        _ = try await allow(fixture, grant: .view)
        let started = await startSession(fixture, mode: .control, keystore: Keystore())
        #expect(started.refusal == .controlSignature)
    }

    @Test func oneControllerAndTwoViewersIsTheLimit() async throws {
        let fixture = makeFixture()
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
        let fixture = makeFixture()
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
        let fixture = makeFixture(confirm: false)
        defer { cleanUp(fixture) }
        try await fixture.service.setAllowed(deviceId: Self.phone, allowed: true)
        try await fixture.service.setGrant(deviceId: Self.phone, grant: .control)
        let refused = await fixture.engine.enrolControlKey(
            deviceId: Self.phone, publicKeyB64: Keystore().publicKeyData.base64EncodedString())
        #expect(refused.refusal == .controlKeyNotConfirmed)
        #expect(await fixture.service.settings(for: Self.phone)?.controlKeyPublicData == nil)
    }

    @Test func enrolmentNeedsTheAllowListAndTheControlGrantFirst() async throws {
        let fixture = makeFixture()
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
        let fixture = makeFixture()
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
        let fixture = makeFixture()
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
        let fixture = makeFixture()
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
        let fixture = makeFixture(load: FakeLoad(headroom: 0.9, thermal: false))
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
        #expect(overview.width == 640)   // the phone's own OVERVIEW_MAX_WIDTH
        #expect(overview.fps == ScreenShareCapturePlanner.overviewFps)
        // The overview really is low-res: it spends a quarter of a pixel on each
        // display pixel, where the zoomed region gets one for one — which is the
        // whole point of streaming the region separately.
        let overviewDensity = Double(overview.width) / bounds.width
        let primaryDensity = Double(primary.width) / primary.sourceRect.width
        #expect(overviewDensity < primaryDensity)
        #expect(overviewDensity <= 1.0 / 3)
    }

    @Test func aDegenerateZoomRegionIsRefusedAndClampedInsideTheDisplay() {
        #expect(ScreenShareZoomRegion(x: 0, y: 0, width: 0.001, height: 0.5).normalized == nil)
        #expect(ScreenShareZoomRegion(x: .nan, y: 0, width: 0.5, height: 0.5).normalized?.x == 0)
        let clamped = ScreenShareZoomRegion(x: 0.8, y: 0.8, width: 0.5, height: 0.5).normalized!
        #expect(clamped.x + clamped.width <= 1.0001)
        #expect(clamped.y + clamped.height <= 1.0001)
    }

    @Test func anUnpluggedDisplayFallsBackToTheMainOne() async throws {
        let fixture = makeFixture()
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

    @Test func theOverviewTravelsAsAThrottledStillAndTheVideoTrackCarriesTheRegion() async throws {
        let stills = FakeStillEncoder()
        let fixture = makeFixture(stills: stills)
        defer { cleanUp(fixture) }
        _ = try await allow(fixture, grant: .view)
        guard case .success(let reply) = await startSession(fixture, mode: .view) else {
            Issue.record("start refused"); return
        }
        let peer = try #require(await fixture.peers.peer(sessionId: reply.sessionId))
        let frame = try #require(Self.pixelFrame(layer: .primary))
        let overviewFrame = try #require(Self.pixelFrame(layer: .overview))

        // The region goes on the video track.
        await fixture.engine.deliver(frame: frame, status: .complete, dirtyRects: 3)
        #expect(await peer.frames == 1)

        // The overview goes on the data channel instead, as a small still.
        await fixture.engine.deliver(frame: overviewFrame, status: .complete, dirtyRects: 3)
        #expect(await peer.frames == 1)
        let stillFrames = await peer.sentData.compactMap {
            (try? JSONSerialization.jsonObject(with: $0)) as? [String: Any]
        }.filter { $0["t"] as? String == "overview" }
        #expect(stillFrames.count == 1)
        #expect((stillFrames.first?["jpegB64"] as? String ?? "").isEmpty == false)

        // And it is throttled: a second overview straight away is not sent.
        await fixture.engine.deliver(frame: overviewFrame, status: .complete, dirtyRects: 3)
        let again = await peer.sentData.compactMap {
            (try? JSONSerialization.jsonObject(with: $0)) as? [String: Any]
        }.filter { $0["t"] as? String == "overview" }
        #expect(again.count == 1)
        #expect(ScreenShareEngine.overviewInterval == 2)
    }

    @Test func eachLayerHasItsOwnIdleGate() async throws {
        let backend = RecordingCaptureBackend()
        let displays = FakeDisplays(layout: [1: CGRect(x: 0, y: 0, width: 1920, height: 1080)], main: 1)
        let capture = ScreenShareCaptureController(backend: backend, displays: displays)
        _ = try await capture.start(displayId: 1, quality: ScreenShareQuality.wifiCeiling)
        // The overview's slow trickle must not consume the primary layer's first
        // frame, nor suppress its keepalive.
        #expect(await capture.admit(layer: .overview, status: .complete, dirtyRects: 0))
        #expect(await capture.admit(layer: .primary, status: .complete, dirtyRects: 0))
        #expect(!(await capture.admit(layer: .primary, status: .complete, dirtyRects: 0)))
        #expect(!(await capture.admit(layer: .overview, status: .idle, dirtyRects: 9)))
    }

    // MARK: Data channel → CGEventPost

    @Test func theDocumentedDataChannelMessagesBecomeInjectedEvents() async throws {
        let fixture = makeFixture()
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

    @Test func aViewOnlyPhoneInjectsNothingHoweverItAsks() async throws {
        let fixture = makeFixture()
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
        let fixture = makeFixture()
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

    @Test func onlyTheTwoClipboardShortcutsAreAccepted() {
        #expect(ScreenShareDataChannel.decode(["t": "key", "combo": "cmd+c"]).isSuccess)
        #expect(ScreenShareDataChannel.decode(["t": "key", "combo": "cmd+v"]).isSuccess)
        // A phone cannot synthesize an arbitrary chord — ⌘Q, say.
        #expect(ScreenShareDataChannel.decode(["t": "key", "combo": "cmd+q"]).rejection == .unknownType)
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
        for frame in frames {
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
            "t": "clipboard", "enc": "raw", "bytes": 50_000_000, "data": "AAAA",
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
        let fixture = makeFixture()
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
        let fixture = makeFixture()
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
        let fixture = makeFixture()
        defer { cleanUp(fixture) }
        _ = try await allow(fixture, grant: .view)
        guard case .success(let reply) = await startSession(fixture, mode: .view) else {
            Issue.record("start refused"); return
        }
        let peer = try #require(await fixture.peers.peer(sessionId: reply.sessionId))
        await peer.emitData(Self.json(["t": "background", "background": true]))
        // The fixture's sleeper makes the 30 s rule a millisecond; the rule itself
        // is the host's, which is the point.
        for _ in 0..<60 where !(await fixture.engine.liveSessionIds.isEmpty) {
            try await Task.sleep(nanoseconds: 10_000_000)
        }
        #expect(await fixture.engine.liveSessionIds.isEmpty)
        #expect(ScreenSharePolicy.backgroundTimeout == 30)
    }

    @Test func aKillSwitchStopsEveryPeerAndTellsEveryPhone() async throws {
        let fixture = makeFixture()
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
        let fixture = makeFixture()
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
        let fixture = makeFixture()
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
        let fixture = makeFixture()
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

    /// A tiny real `CVPixelBuffer`, so the delivery path runs exactly as it does
    /// with a captured frame.
    static func pixelFrame(layer: ScreenShareCaptureLayer.Kind) -> ScreenShareVideoFrame? {
        var buffer: CVPixelBuffer?
        let status = CVPixelBufferCreate(kCFAllocatorDefault, 16, 16,
                                         kCVPixelFormatType_32BGRA, nil, &buffer)
        guard status == kCVReturnSuccess, let buffer else { return nil }
        return ScreenShareVideoFrame(pixelBuffer: buffer, layer: layer, timestampNanos: 1)
    }
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

    func addRemoteCandidate(candidate: String, sdpMid: String?, sdpMLineIndex: Int?) async {
        remoteCandidates.append(candidate)
    }

    func send(frame: ScreenShareVideoFrame) async { frames += 1 }

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

    func setFailure(_ value: ScreenShareCaptureError?) { failure = value }

    func apply(_ plan: ScreenShareCapturePlan) async throws {
        if let failure { throw failure }
        plans.append(plan)
    }

    func stop() async { stops += 1 }

    var lastPlan: ScreenShareCapturePlan? { plans.last }
}

/// The Mac pasteboard, as a test decides it. A lock rather than an actor, because
/// the protocol is synchronous: `NSPasteboard` is.
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

/// Stands in for the CoreImage JPEG encoder; the bytes do not matter, only that
/// the overview takes the data-channel path and is throttled.
struct FakeStillEncoder: ScreenShareStillEncoder {
    func jpeg(_ frame: ScreenShareVideoFrame, quality: Double) -> Data? {
        Data(repeating: 0xAB, count: 512)
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
