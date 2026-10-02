import CryptoKit
import Foundation
import os

// MARK: - Route payloads

/// `GET /m1/screen-share/state` (docs/relay.md).
public struct ScreenShareStateBody: Encodable, Sendable, Equatable {
    public var allowed: Bool
    public var grant: String
    public var isBeta: Bool
    public var displays: [ScreenShareDisplayInfo]
    public var controlChallengeB64: String?
    /// Short, non-secret fingerprint of the control key this Mac has stored for
    /// the phone, or nil when none is enrolled. The phone reads this to decide
    /// whether to enrol — never a biometric prompt just to open the screen.
    public var controlKeyFingerprint: String?
    public var iceServers: [ScreenShareIceServerBody]?
    public var idleTimeoutSeconds: Int

    public init(allowed: Bool, grant: String, isBeta: Bool = true,
                displays: [ScreenShareDisplayInfo], controlChallengeB64: String?,
                controlKeyFingerprint: String?, iceServers: [ScreenShareIceServerBody]?,
                idleTimeoutSeconds: Int) {
        self.allowed = allowed; self.grant = grant; self.isBeta = isBeta
        self.displays = displays; self.controlChallengeB64 = controlChallengeB64
        self.controlKeyFingerprint = controlKeyFingerprint
        self.iceServers = iceServers; self.idleTimeoutSeconds = idleTimeoutSeconds
    }
}

public struct ScreenShareIceServerBody: Encodable, Sendable, Equatable {
    public var urls: [String]
    public var username: String?
    public var credential: String?

    public init(_ server: ScreenShareIceServer) {
        urls = server.urls; username = server.username; credential = server.credential
    }
}

public struct ScreenShareStateEnvelope: Encodable, Sendable, Equatable {
    public var screenShare: ScreenShareStateBody
    public init(screenShare: ScreenShareStateBody) { self.screenShare = screenShare }
}

/// `POST /m1/screen-share/sessions` request and reply.
public struct ScreenShareSessionRequestBody: Decodable, Sendable {
    public var mode: String
    public var displayId: UInt32?
    public var controlSignatureB64: String?
    public var network: String?
    public var decodes: [String]?
}

public struct ScreenShareSessionReply: Encodable, Sendable, Equatable {
    public var sessionId: String
    public var mode: String
    public var displayId: UInt32
    public var codec: String
    public var quality: ScreenShareQualityProfile

    public init(sessionId: String, mode: String, displayId: UInt32, codec: String,
                quality: ScreenShareQualityProfile) {
        self.sessionId = sessionId; self.mode = mode; self.displayId = displayId
        self.codec = codec; self.quality = quality
    }
}

/// `POST /m1/screen-share/control-key`.
public struct ScreenShareControlKeyRequestBody: Decodable, Sendable {
    public var publicKeyB64: String
}

public struct ScreenShareControlKeyReply: Encodable, Sendable, Equatable {
    public var fingerprint: String
    public init(fingerprint: String) { self.fingerprint = fingerprint }
}

/// Every way a screen-share route refuses, with the `reason` of the contract.
public enum ScreenShareRouteRefusal: String, Error, Sendable, Equatable {
    case legacyClient = "legacy-client"
    case deviceNotAllowed = "device-not-allowed"
    case insufficientGrant = "insufficient-grant"
    case controlSignature = "control-signature"
    case concurrencyLimit = "concurrency-limit"
    case screenPermission = "screen-permission"
    case badRequest = "bad-request"
    /// A control key is already stored: the Mac never silently replaces one.
    case controlKeyPresent = "control-key-present"
    /// The person at the Mac did not confirm the key's fingerprint.
    case controlKeyNotConfirmed = "control-key-not-confirmed"

    public var status: Int {
        switch self {
        case .badRequest: return 400
        case .controlKeyPresent: return 409
        default: return 403
        }
    }
}

/// Asks the person at the Mac to confirm a control key's fingerprint before it
/// is stored. Granting control is their decision, and so is accepting the key
/// that will prove it: a phone cannot enrol one behind their back.
public protocol ScreenShareControlKeyConfirmer: Sendable {
    func confirmControlKey(deviceId: String, fingerprint: String) async -> Bool
}

public enum ScreenShareControlKey {
    /// Short, non-secret fingerprint shown on both the Mac and the phone:
    /// SHA-256 of the public key, first 8 bytes, in four-character groups.
    public static func fingerprint(_ publicKey: Data) -> String {
        let digest = SHA256.hash(data: publicKey).prefix(8).map { String(format: "%02X", $0) }.joined()
        return stride(from: 0, to: digest.count, by: 4).map {
            String(digest.dropFirst($0).prefix(4))
        }.joined(separator: "-")
    }

    /// An ANSI X9.62 uncompressed P-256 point, which is what the Android
    /// Keystore exports. Anything else is refused before it is stored.
    public static func isValid(_ publicKey: Data) -> Bool {
        publicKey.count == 65 && publicKey.first == 0x04
            && (try? P256.Signing.PublicKey(x963Representation: publicKey)) != nil
    }
}

// MARK: - Engine

/// Runs screen sharing end to end on the Mac: the routes, the signalling, the
/// capture, the peer connections, the data channel and the TURN renewal.
///
/// Every safety decision still belongs to `ScreenShareService`/`ScreenShareHost`.
/// This actor is what turns those decisions into a stream and an injected event,
/// and what tells the phone what happened.
public actor ScreenShareEngine {
    private static let log = Logger(subsystem: "dev.mightyclaude.native", category: "screen-share-engine")

    // MARK: Dependencies

    private let service: ScreenShareService
    private let peers: ScreenSharePeerFactory
    private let capture: ScreenShareCaptureController
    private let signals: ScreenShareSignalSender
    private let turn: ScreenShareTurnSource
    private let load: ScreenShareMachineLoad
    private let displays: ScreenShareDisplaySource
    private let pasteboard: ScreenSharePasteboard
    private let compressor: ScreenShareCompressor?
    private let confirmer: ScreenShareControlKeyConfirmer
    private let stills: ScreenShareStillEncoder?
    private let now: @Sendable () -> Date
    private let sleeper: @Sendable (TimeInterval) async throws -> Void

    // MARK: State

    private struct Session {
        var deviceId: String
        var mode: ScreenShareGrant
        var displayId: UInt32
        var codec: ScreenShareVideoCodec
        var quality: ScreenShareQualityProfile
        var network: ScreenShareNetworkKind
        var decodes: [ScreenShareVideoCodec]
        var peer: ScreenSharePeerConnection
        var path: ScreenShareIcePath?
        var clipboard = ScreenShareClipboardAssembler()
        var renewal: Task<Void, Never>?
        var background: Task<Void, Never>?
    }

    private var sessions: [String: Session] = [:]
    private var credential: ScreenShareTurnCredential?
    private var framesBlocked = false
    private var lastOverview: Date?

    public init(
        service: ScreenShareService,
        peers: ScreenSharePeerFactory,
        capture: ScreenShareCaptureController,
        signals: ScreenShareSignalSender,
        turn: ScreenShareTurnSource,
        displays: ScreenShareDisplaySource,
        pasteboard: ScreenSharePasteboard,
        confirmer: ScreenShareControlKeyConfirmer,
        load: ScreenShareMachineLoad = SystemScreenShareLoad(),
        compressor: ScreenShareCompressor? = nil,
        stills: ScreenShareStillEncoder? = nil,
        now: @escaping @Sendable () -> Date = { Date() },
        sleeper: @escaping @Sendable (TimeInterval) async throws -> Void = { seconds in
            try await Task.sleep(nanoseconds: UInt64(max(0, seconds) * 1_000_000_000))
        }
    ) {
        self.service = service; self.peers = peers; self.capture = capture
        self.signals = signals; self.turn = turn; self.displays = displays
        self.pasteboard = pasteboard; self.confirmer = confirmer
        self.load = load; self.compressor = compressor; self.stills = stills
        self.now = now; self.sleeper = sleeper
    }

    // MARK: GET /m1/screen-share/state

    public func state(deviceId: String) async -> Result<ScreenShareStateEnvelope, ScreenShareRouteRefusal> {
        guard deviceId != MobileDeviceRegistry.legacyId else { return .failure(.legacyClient) }
        let row = await service.settings(for: deviceId)
        let grant = row?.grant ?? .none
        let allowed = row?.allowed == true
        var challenge: String?
        // A challenge is minted only for a phone that may actually control, and
        // the session id it is bound to is the one the phone will start with.
        if allowed, grant == .control {
            challenge = await service.controlChallenge(sessionId: Self.challengeSession(deviceId)).base64EncodedString()
        }
        let servers = allowed ? await iceServers()?.map(ScreenShareIceServerBody.init) : nil
        let body = ScreenShareStateBody(
            allowed: allowed, grant: grant.rawValue, displays: displays.info(),
            controlChallengeB64: challenge,
            controlKeyFingerprint: row?.controlKeyPublicData.map(ScreenShareControlKey.fingerprint),
            iceServers: servers,
            idleTimeoutSeconds: Int(ScreenSharePolicy.idleTimeout(for: grant == .control ? .control : .view)))
        return .success(ScreenShareStateEnvelope(screenShare: body))
    }

    /// The session id a `state` challenge is bound to. A control start reuses it,
    /// so the signature covers the challenge the Mac actually handed out.
    public static func challengeSession(_ deviceId: String) -> String { "pending:" + deviceId }

    // MARK: POST /m1/screen-share/control-key

    /// Stores the phone's biometric-gated Keystore public key.
    ///
    /// A key is accepted only when none is stored for this phone and the person
    /// at the Mac confirms the fingerprint they are shown. There is no silent
    /// replacement: a phone that lost its key has to have the Mac user withdraw
    /// and re-grant control, which clears the old key first.
    public func enrolControlKey(
        deviceId: String, publicKeyB64: String
    ) async -> Result<ScreenShareControlKeyReply, ScreenShareRouteRefusal> {
        guard deviceId != MobileDeviceRegistry.legacyId else { return .failure(.legacyClient) }
        guard let key = Data(base64Encoded: publicKeyB64), ScreenShareControlKey.isValid(key) else {
            return .failure(.badRequest)
        }
        guard let row = await service.settings(for: deviceId), row.allowed else {
            return .failure(.deviceNotAllowed)
        }
        guard row.grant == .control else { return .failure(.insufficientGrant) }
        if let stored = row.controlKeyPublicData {
            // Already enrolled. The same key again is not an error — a phone that
            // retried a request it never saw answered gets the same answer.
            guard stored != key else {
                return .success(ScreenShareControlKeyReply(fingerprint: ScreenShareControlKey.fingerprint(stored)))
            }
            return .failure(.controlKeyPresent)
        }
        let fingerprint = ScreenShareControlKey.fingerprint(key)
        guard await confirmer.confirmControlKey(deviceId: deviceId, fingerprint: fingerprint) else {
            return .failure(.controlKeyNotConfirmed)
        }
        // The grant may have been withdrawn while the dialog was open.
        guard let fresh = await service.settings(for: deviceId), fresh.allowed,
              fresh.grant == .control, fresh.controlKeyPublicData == nil
        else { return .failure(.insufficientGrant) }
        do { try await service.setGrant(deviceId: deviceId, grant: .control, controlKeyPublicData: key) }
        catch { return .failure(.badRequest) }
        await pushGrant(to: deviceId)
        return .success(ScreenShareControlKeyReply(fingerprint: fingerprint))
    }

    // MARK: POST /m1/screen-share/sessions

    public func start(
        deviceId: String, request: ScreenShareSessionRequestBody
    ) async -> Result<ScreenShareSessionReply, ScreenShareRouteRefusal> {
        guard deviceId != MobileDeviceRegistry.legacyId else { return .failure(.legacyClient) }
        guard let mode = ScreenShareGrant(rawValue: request.mode), mode != .none else {
            return .failure(.badRequest)
        }
        let network = ScreenShareNetworkKind(rawValue: request.network ?? "wifi") ?? .wifi
        let decodes = (request.decodes ?? []).compactMap(ScreenShareVideoCodec.init(rawValue:))
        let requestedDisplay = request.displayId ?? displays.mainDisplayId()
        guard let bounds = displays.bounds(of: requestedDisplay) ?? displays.bounds(of: displays.mainDisplayId()) else {
            return .failure(.screenPermission)
        }

        let sessionId = UUID().uuidString
        let codec = ScreenShareCodecPolicy.choose(ScreenShareCodecConditions(
            network: network, phoneDecodes: decodes,
            cpuHeadroom: load.cpuHeadroom(), thermalPressure: load.thermalPressure()))
        let quality = ScreenShareQuality.profile(
            network: network, displayWidth: Int(bounds.width), displayHeight: Int(bounds.height))

        // The challenge the phone signed was handed out by `state`, which binds
        // it to a per-device id; the host verifies against that same id.
        var signature: Data?
        var challenge: Data?
        if mode == .control {
            guard let raw = request.controlSignatureB64, let decoded = Data(base64Encoded: raw),
                  decoded.count <= 256
            else { return .failure(.controlSignature) }
            signature = decoded
            // The challenge was handed out by `state`, before the phone could
            // know a session id; it moves onto this session so `join` spends it
            // there and the same signature can never start a second one.
            challenge = await service.adoptChallenge(
                from: Self.challengeSession(deviceId), to: sessionId)
            guard challenge != nil else { return .failure(.controlSignature) }
        }

        let servers = await iceServers() ?? []
        let events = ScreenSharePeerEvents(
            localCandidate: { [weak self] candidate, mid, index, fragment in
                await self?.localCandidate(sessionId: sessionId, candidate: candidate,
                                           sdpMid: mid, sdpMLineIndex: index, usernameFragment: fragment)
            },
            failed: { [weak self] in await self?.peerFailed(sessionId: sessionId) },
            connected: { [weak self] path in await self?.peerConnected(sessionId: sessionId, path: path) },
            data: { [weak self] data in await self?.dataChannel(sessionId: sessionId, data: data) })

        let peer: ScreenSharePeerConnection
        do { peer = try await peers.makePeer(sessionId: sessionId, iceServers: servers, events: events) }
        catch { return .failure(.screenPermission) }

        let surface = ScreenShareSurface(
            startCapture: { [weak self] in await self?.startCapture(sessionId: sessionId) },
            stopCapture: { [weak self] in await self?.stopCapture() },
            closePeer: { await peer.close() })

        let admitted = await service.join(
            sessionId: sessionId, deviceId: deviceId, mode: mode,
            controlChallenge: challenge, controlSignature: signature, surface: surface)
        if case .failure(let error) = admitted {
            await peer.close()
            // `join` spends the challenge whether it admits the session or not,
            // so a refused control attempt has to ask for a new one. This only
            // clears a challenge a refusal never reached.
            await service.discardChallenge(sessionId: sessionId)
            return .failure(Self.refusal(error))
        }

        var session = Session(
            deviceId: deviceId, mode: mode, displayId: requestedDisplay, codec: codec,
            quality: quality, network: network, decodes: decodes, peer: peer)

        // Screen Recording may be missing or expired after an update; the phone
        // is told to ask the person at the Mac rather than shown a black frame.
        let captured: UInt32
        do { captured = try await capture.start(displayId: requestedDisplay, quality: quality) }
        catch {
            await service.endSession(sessionId: sessionId, reason: .peerLeft)
            await peer.close()
            return .failure(.screenPermission)
        }
        session.displayId = captured

        // The offer leaves only after the session exists and capture is running,
        // so a phone can never answer a session the Mac has already refused.
        do {
            let sdp = try await peer.createOffer(codec: codec, quality: quality, iceRestart: false)
            session.renewal = renewalTask(sessionId: sessionId)
            sessions[sessionId] = session
            await signals.send(.offer(sessionId: sessionId, sdp: sdp, mode: mode, displayId: captured,
                                      codec: codec, quality: quality, iceRestart: false), to: deviceId)
        } catch {
            await service.endSession(sessionId: sessionId, reason: .peerLeft)
            await peer.close()
            return .failure(.screenPermission)
        }

        return .success(ScreenShareSessionReply(
            sessionId: sessionId, mode: mode.rawValue, displayId: captured,
            codec: codec.rawValue, quality: quality))
    }

    static func refusal(_ error: ScreenShareError) -> ScreenShareRouteRefusal {
        switch error {
        case .deviceNotAllowed: return .deviceNotAllowed
        case .insufficientGrant: return .insufficientGrant
        case .controlSignatureInvalid: return .controlSignature
        case .concurrencyLimit: return .concurrencyLimit
        case .sessionStopped: return .deviceNotAllowed
        }
    }

    // MARK: Inbound signalling

    /// One `screen-answer`, `screen-ice` or `screen-session-end` from a phone.
    /// A frame naming a session that phone does not own is dropped.
    public func handle(_ signal: ScreenShareSignal, from deviceId: String) async {
        guard let sessionId = signal.sessionId, let session = sessions[sessionId],
              session.deviceId == deviceId
        else { return }
        switch signal {
        case .answer(_, let sdp):
            do { try await session.peer.acceptAnswer(sdp) }
            catch { await end(sessionId: sessionId, reason: .peerLeft) }
        case .ice(_, let candidate, let mid, let index, _):
            await session.peer.addRemoteCandidate(candidate: candidate, sdpMid: mid, sdpMLineIndex: index)
        case .sessionEnd:
            await end(sessionId: sessionId, reason: .peerLeft)
        default:
            // Host-only types: a phone echoing one back is not the Mac.
            break
        }
    }

    private func localCandidate(
        sessionId: String, candidate: String, sdpMid: String?, sdpMLineIndex: Int?, usernameFragment: String?
    ) async {
        guard let session = sessions[sessionId] else { return }
        await signals.send(.ice(sessionId: sessionId, candidate: candidate, sdpMid: sdpMid,
                                sdpMLineIndex: sdpMLineIndex, usernameFragment: usernameFragment),
                           to: session.deviceId)
    }

    private func peerConnected(sessionId: String, path: ScreenShareIcePath?) async {
        guard var session = sessions[sessionId], session.path != path else { return }
        // No suspension before the write-back below, so the snapshot is current.
        session.path = path
        // A relayed path has a bandwidth quota, so the sender caps itself to it
        // rather than letting coturn drop packets.
        let fresh = ScreenShareQuality.profile(
            network: session.network, path: path ?? .host,
            displayWidth: session.quality.width, displayHeight: session.quality.height)
        if fresh.maxBitrateKbps != session.quality.maxBitrateKbps {
            session.quality.maxBitrateKbps = fresh.maxBitrateKbps
            sessions[sessionId] = session
            await renegotiate(sessionId: sessionId, iceRestart: false)
        } else {
            sessions[sessionId] = session
        }
    }

    private func peerFailed(sessionId: String) async {
        await end(sessionId: sessionId, reason: .peerLeft)
    }

    // MARK: Data channel

    private func dataChannel(sessionId: String, data: Data) async {
        guard let session = sessions[sessionId] else { return }
        switch ScreenShareDataChannel.decode(data) {
        case .failure:
            // A frame the Mac does not understand is dropped in silence; nothing
            // about its contents is logged.
            return
        case .success(let message):
            await apply(message, sessionId: sessionId, session: session)
        }
    }

    private func apply(_ message: ScreenShareDataMessage, sessionId: String, session: Session) async {
        switch message {
        case .input(let event):
            // The host refuses everything a view-only phone sends, everything
            // while the screen is locked or secure input is on, and everything
            // for 2 s after the person at the Mac touched the keyboard.
            _ = await service.deliver(event, sessionId: sessionId)
        case .zoom(let displayId, let region):
            guard displayId == session.displayId else { return }
            try? await capture.setZoom(region)
            await service.noteFrameDelivered(sessionId: sessionId)
        case .display(let displayId):
            await switchDisplay(sessionId: sessionId, to: displayId)
        case .clipboard(let frame):
            await acceptClipboard(frame, sessionId: sessionId)
        case .clipboardRequest:
            await sendClipboard(sessionId: sessionId)
        case .background(let backgrounded):
            await setBackground(backgrounded, sessionId: sessionId)
        }
    }

    private func switchDisplay(sessionId: String, to displayId: UInt32) async {
        guard sessions[sessionId] != nil else { return }
        let captured: UInt32
        do { captured = try await capture.setDisplay(displayId) }
        catch { await end(sessionId: sessionId, reason: .peerLeft); return }
        // The actor suspended while capture was reconfiguring, so the session may
        // be gone by now; writing a stale copy back would resurrect it.
        guard var session = sessions[sessionId] else { return }
        session.displayId = captured
        sessions[sessionId] = session
        // A new display means new geometry, so the stream is renegotiated with
        // an ICE restart exactly as the contract says.
        await renegotiate(sessionId: sessionId, iceRestart: true)
    }

    /// The phone's clipboard, reassembled and pasted. Control grant only, and
    /// the host is what enforces that.
    private func acceptClipboard(_ frame: ScreenShareClipboardFrame, sessionId: String) async {
        guard sessions[sessionId] != nil else { return }
        guard await service.mayInject(sessionId: sessionId) else { return }
        // Asking the host was a suspension: a kill may have taken the session,
        // and a half-assembled transfer must not bring it back.
        guard var session = sessions[sessionId] else { return }
        let outcome = session.clipboard.accept(frame, now: now())
        sessions[sessionId] = session
        guard case .complete(let encoding, let declared, let payload) = outcome else { return }
        guard case .success(let text) = ScreenShareClipboardCodec.decode(
            encoding: encoding, declaredBytes: declared, payload: payload, compressor: compressor)
        else { return }
        pasteboard.write(text)
        await service.noteActivity(sessionId: sessionId)
    }

    /// The Mac's clipboard, sent once because the user pressed the button.
    /// A concealed pasteboard item is never read: the phone is told it was
    /// skipped rather than handed an empty paste.
    private func sendClipboard(sessionId: String) async {
        guard sessions[sessionId] != nil else { return }
        guard await service.mayInject(sessionId: sessionId) else { return }
        guard let session = sessions[sessionId] else { return }
        let read = pasteboard.read()
        if read.concealed {
            _ = await session.peer.sendData(Self.encode([
                "t": "clipboard", "dir": "to-phone", "enc": "raw", "bytes": 0,
                "id": UUID().uuidString, "seq": 0, "total": 1, "data": "", "concealed": true,
            ]))
            return
        }
        guard let text = read.text,
              let packed = ScreenShareClipboardCodec.encode(text: text, compressor: compressor)
        else { return }
        let id = UUID().uuidString
        for object in ScreenShareDataChannel.clipboardFrames(
            id: id, encoding: packed.encoding, plaintextBytes: packed.plaintextBytes, payload: packed.payload) {
            guard await session.peer.sendData(Self.encode(object)) else { return }
        }
        await service.noteActivity(sessionId: sessionId)
    }

    static func encode(_ object: [String: Any]) -> Data {
        (try? JSONSerialization.data(withJSONObject: object, options: [.sortedKeys])) ?? Data()
    }

    /// The phone reported that it went to the background. The 30 s rule is the
    /// host's: the phone only says what it is doing, and a phone that never says
    /// anything again still hits the idle timeout.
    private func setBackground(_ backgrounded: Bool, sessionId: String) async {
        guard var session = sessions[sessionId] else { return }
        session.background?.cancel()
        session.background = nil
        guard backgrounded else {
            sessions[sessionId] = session
            await service.noteActivity(sessionId: sessionId)
            return
        }
        session.background = Task { [weak self, sleeper] in
            try? await sleeper(ScreenSharePolicy.backgroundTimeout)
            guard !Task.isCancelled else { return }
            await self?.end(sessionId: sessionId, reason: .peerLeft)
        }
        sessions[sessionId] = session
    }

    // MARK: Capture

    /// The host asks for capture when the first session starts, and again once a
    /// lock screen or a password field has cleared.
    private func startCapture(sessionId: String) async {
        guard let session = sessions[sessionId] ?? sessions.values.first else { return }
        _ = try? await capture.start(displayId: session.displayId, quality: session.quality)
    }

    private func stopCapture() async { await capture.stop() }

    /// One captured frame. Idle frames are dropped before an encoder is woken,
    /// which is what keeps an untouched screen near zero traffic.
    ///
    /// The video track carries the primary layer only — the region the user is
    /// reading, at the full pixel budget. The whole-screen overview goes down the
    /// data channel as an occasional small still, so a zoomed phone has something
    /// real underneath its window without a second video stream.
    public func deliver(frame: ScreenShareVideoFrame, status: ScreenShareFrameStatus, dirtyRects: Int) async {
        guard await capture.admit(layer: frame.layer, status: status, dirtyRects: dirtyRects) else { return }
        guard frame.layer == .primary else { await deliverOverview(frame); return }
        for (sessionId, session) in sessions {
            await session.peer.send(frame: frame)
            await service.noteFrameDelivered(sessionId: sessionId)
        }
    }

    /// How often an overview still is sent at most. The overview is a backdrop,
    /// not the picture: a slow refresh is what keeps it nearly free.
    public static let overviewInterval: TimeInterval = 2
    /// JPEG quality for the overview still. Low: it is a 640-pixel backdrop.
    public static let overviewJPEGQuality = 0.5

    private func deliverOverview(_ frame: ScreenShareVideoFrame) async {
        guard let stills, !sessions.isEmpty else { return }
        if let lastOverview, now().timeIntervalSince(lastOverview) < Self.overviewInterval { return }
        guard let jpeg = stills.jpeg(frame, quality: Self.overviewJPEGQuality),
              jpeg.count <= Self.maximumOverviewBytes
        else { return }
        lastOverview = now()
        let message = Self.encode([
            "t": "overview", "displayId": NSNumber(value: overviewDisplayId()),
            "jpegB64": jpeg.base64EncodedString(),
        ])
        for session in sessions.values { _ = await session.peer.sendData(message) }
    }

    /// A still larger than this is dropped rather than sent: a 640-pixel JPEG
    /// that big means the encoder produced something unexpected.
    static let maximumOverviewBytes = 256 * 1_024

    private func overviewDisplayId() -> UInt32 {
        sessions.values.first?.displayId ?? displays.mainDisplayId()
    }

    // MARK: TURN renewal

    private func iceServers() async -> [ScreenShareIceServer]? {
        if let credential, !credential.needsRenewal(now: now()) { return credential.iceServers }
        guard let fresh = await turn.mintTurnCredential() else { return credential?.iceServers }
        credential = fresh
        return fresh.iceServers
    }

    /// Waits until the credential is close to expiry, mints a new one, hands it
    /// to the phone in a `screen-grant` and renegotiates with an ICE restart —
    /// with the renewed servers already on the peer, so the restart gathers
    /// against them rather than the pair about to expire.
    private func renewalTask(sessionId: String) -> Task<Void, Never> {
        Task { [weak self, sleeper] in
            while !Task.isCancelled {
                guard let delay = await self?.renewalDelay() else { return }
                try? await sleeper(delay)
                guard !Task.isCancelled else { return }
                await self?.renewCredential(sessionId: sessionId)
            }
        }
    }

    private func renewalDelay() -> TimeInterval {
        guard let credential else { return ScreenShareTurnCredential.renewalMargin }
        let until = credential.expiresAt.addingTimeInterval(-ScreenShareTurnCredential.renewalMargin)
        return max(1, until.timeIntervalSince(now()))
    }

    func renewCredential(sessionId: String) async {
        guard let session = sessions[sessionId] else { return }
        guard let fresh = await turn.mintTurnCredential() else { return }
        credential = fresh
        let servers = fresh.iceServers
        await session.peer.setConfiguration(iceServers: servers)
        await signals.send(.grant(sessionId: sessionId, allowed: true, grant: session.mode,
                                  controlChallengeB64: nil, iceServers: servers, displays: nil),
                           to: session.deviceId)
        await renegotiate(sessionId: sessionId, iceRestart: true)
    }

    private func renegotiate(sessionId: String, iceRestart: Bool) async {
        guard let session = sessions[sessionId] else { return }
        guard let sdp = try? await session.peer.createOffer(
            codec: session.codec, quality: session.quality, iceRestart: iceRestart)
        else { await end(sessionId: sessionId, reason: .peerLeft); return }
        // The session may have been killed while the offer was being made; an
        // offer for a session that no longer exists must never go out.
        guard sessions[sessionId] != nil else { return }
        await signals.send(.offer(sessionId: sessionId, sdp: sdp, mode: session.mode,
                                  displayId: session.displayId, codec: session.codec,
                                  quality: session.quality, iceRestart: iceRestart),
                           to: session.deviceId)
    }

    // MARK: Stopping

    /// Ends one session on the Mac and tells the phone.
    public func end(sessionId: String, reason: ScreenShareStopReason) async {
        guard let session = sessions.removeValue(forKey: sessionId) else { return }
        session.renewal?.cancel()
        session.background?.cancel()
        await service.endSession(sessionId: sessionId, reason: reason)
        if let kill = ScreenShareWireReason.kill(reason) {
            await signals.send(.kill(sessionId: sessionId, reason: kill), to: session.deviceId)
        } else {
            await signals.send(.sessionEnd(sessionId: sessionId,
                                           reason: ScreenShareWireReason.sessionEnd(reason)),
                               to: session.deviceId)
        }
    }

    /// Called after the host stopped sessions on its own (kill switch, revoke,
    /// downgrade, rekey, idle timeout). The host has already stopped capture,
    /// injection and the peers; this drops the engine's own bookkeeping and
    /// sends the courtesy note.
    public func hostStopped(_ stopped: [ScreenShareStoppedSession]) async {
        for entry in stopped {
            guard let session = sessions.removeValue(forKey: entry.sessionId) else { continue }
            session.renewal?.cancel()
            session.background?.cancel()
            await session.peer.close()
            if let kill = ScreenShareWireReason.kill(entry.reason) {
                await signals.send(.kill(sessionId: entry.sessionId, reason: kill), to: entry.deviceId)
            } else {
                await signals.send(.sessionEnd(sessionId: entry.sessionId,
                                               reason: ScreenShareWireReason.sessionEnd(entry.reason)),
                                   to: entry.deviceId)
            }
        }
    }

    /// The lock screen came on, or a password field took focus. The host has
    /// already stopped the frames; the sessions go too, so their slots are free
    /// and the phone is told why rather than watching a frozen picture.
    public func framesBlockedChanged(_ blocked: Bool, reason: ScreenShareStopReason) async {
        guard blocked != framesBlocked else { return }
        framesBlocked = blocked
        guard blocked else { return }
        for sessionId in sessions.keys { await end(sessionId: sessionId, reason: reason) }
    }

    /// Settings changed for a phone: it learns its new allow-list and grant
    /// state even when it has no session open.
    public func pushGrant(to deviceId: String) async {
        let row = await service.settings(for: deviceId)
        let grant = row?.grant ?? .none
        let allowed = row?.allowed == true
        var challenge: String?
        if allowed, grant == .control {
            challenge = await service.controlChallenge(sessionId: Self.challengeSession(deviceId)).base64EncodedString()
        }
        await signals.send(.grant(sessionId: nil, allowed: allowed, grant: grant,
                                  controlChallengeB64: challenge,
                                  iceServers: allowed ? await iceServers() : nil,
                                  displays: displays.info()),
                           to: deviceId)
    }

    // MARK: Read-only state

    public var liveSessionIds: [String] { sessions.keys.sorted() }
    public func session(_ sessionId: String) -> (deviceId: String, mode: ScreenShareGrant, displayId: UInt32, codec: ScreenShareVideoCodec, quality: ScreenShareQualityProfile)? {
        guard let session = sessions[sessionId] else { return nil }
        return (session.deviceId, session.mode, session.displayId, session.codec, session.quality)
    }
    public func turnCredential() -> ScreenShareTurnCredential? { credential }
    public func stats(sessionId: String) async -> ScreenSharePeerStats? {
        guard let session = sessions[sessionId] else { return nil }
        return await session.peer.statistics()
    }
}
