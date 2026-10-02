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
    /// The Mac is on the lock screen, or a password field holds secure input:
    /// no frames may flow, so no session starts.
    case lockScreen = "lock-screen"
    case secureInput = "secure-input"
    /// A kill (kill switch, revoke, downgrade, rekey) landed while this session
    /// was starting.
    case sessionStopped = "session-stopped"
    case badRequest = "bad-request"
    /// A control key is already stored: the Mac never silently replaces one.
    case controlKeyPresent = "control-key-present"
    /// The person at the Mac is still looking at this phone's fingerprint.
    case controlKeyPending = "control-key-pending"
    /// The person at the Mac did not confirm the key's fingerprint.
    case controlKeyNotConfirmed = "control-key-not-confirmed"

    public var status: Int {
        switch self {
        case .badRequest: return 400
        case .controlKeyPresent, .controlKeyPending: return 409
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
/// Capture is started only through the surface the host is handed, so the
/// host's lock-screen, secure-input and kill rules apply to the very first
/// frame. This actor turns the host's decisions into a stream and an injected
/// event, and tells the phone what happened.
public actor ScreenShareEngine {
    private static let log = Logger(subsystem: "dev.mightyclaude.native", category: "screen-share-engine")

    /// A peer that has not connected this long after its offer went out is
    /// given up on, so a phone that vanished mid-start never holds capture.
    public static let connectDeadline: TimeInterval = 30
    /// How often a running non-H.264 session checks the Mac's CPU and heat.
    public static let loadCheckInterval: TimeInterval = 10
    /// A failed TURN renewal is retried this often until the old credential
    /// expires; the relay's own rate limit stays far away.
    public static let renewalRetry: TimeInterval = 60

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
    private let now: @Sendable () -> Date
    private let sleeper: @Sendable (TimeInterval) async throws -> Void
    private let frameClock: @Sendable () -> Int64

    // MARK: State

    private struct Session {
        var deviceId: String
        var mode: ScreenShareGrant
        var displayId: UInt32
        var codec: ScreenShareVideoCodec
        var quality: ScreenShareQualityProfile
        var network: ScreenShareNetworkKind
        var peer: ScreenSharePeerConnection
        var turnExpiresAt: Date?
        var path: ScreenShareIcePath?
        var connected = false
        var clipboard = ScreenShareClipboardAssembler()
        /// The first offer, held until the route's reply has gone out so the
        /// phone always knows the session id before the offer arrives.
        var pendingOffer: String?
        /// Candidates gathered before that first offer left, in order.
        var queuedCandidates: [ScreenShareSignal] = []
        var tasks: [Task<Void, Never>] = []
        var background: Task<Void, Never>?

        init(deviceId: String, mode: ScreenShareGrant, displayId: UInt32, codec: ScreenShareVideoCodec,
             quality: ScreenShareQualityProfile, network: ScreenShareNetworkKind,
             peer: ScreenSharePeerConnection, turnExpiresAt: Date?) {
            self.deviceId = deviceId; self.mode = mode; self.displayId = displayId; self.codec = codec
            self.quality = quality; self.network = network; self.peer = peer; self.turnExpiresAt = turnExpiresAt
        }
    }

    /// What a session being admitted needs for its capture start, before it
    /// is a full session.
    private struct Starting {
        var displayId: UInt32
        var quality: ScreenShareQualityProfile
        var captureFailed = false
    }

    private var sessions: [String: Session] = [:]
    private var starting: [String: Starting] = [:]
    private var credential: ScreenShareTurnCredential?
    private var framesBlocked = false
    private var blockReason: ScreenShareStopReason = .lockScreen
    /// Phones whose control-key fingerprint is on screen at the Mac right now.
    private var confirming: Set<String> = []
    /// The last admitted frame of each layer: what a newly connected phone, or
    /// a decoder that lost a keyframe while the screen stood still, is sent.
    private var lastFrames: [ScreenShareCaptureLayer.Kind: ScreenShareVideoFrame] = [:]
    private var lastFrameAt: Date?
    private var refresher: Task<Void, Never>?
    private var loadWatch: Task<Void, Never>?

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
        now: @escaping @Sendable () -> Date = { Date() },
        sleeper: @escaping @Sendable (TimeInterval) async throws -> Void = { seconds in
            try await Task.sleep(nanoseconds: UInt64(max(0, seconds) * 1_000_000_000))
        },
        frameClock: @escaping @Sendable () -> Int64 = { Int64(DispatchTime.now().uptimeNanoseconds) }
    ) {
        self.service = service; self.peers = peers; self.capture = capture
        self.signals = signals; self.turn = turn; self.displays = displays
        self.pasteboard = pasteboard; self.confirmer = confirmer
        self.load = load; self.compressor = compressor; self.now = now; self.sleeper = sleeper
        self.frameClock = frameClock
    }

    // MARK: GET /m1/screen-share/state

    public func state(deviceId: String) async -> Result<ScreenShareStateEnvelope, ScreenShareRouteRefusal> {
        guard deviceId != MobileDeviceRegistry.legacyId else { return .failure(.legacyClient) }
        let row = await service.settings(for: deviceId)
        let grant = row?.grant ?? .none
        let allowed = row?.allowed == true
        let body = ScreenShareStateBody(
            allowed: allowed, grant: grant.rawValue, displays: displays.info(),
            controlChallengeB64: await challenge(for: deviceId, allowed: allowed, grant: grant),
            controlKeyFingerprint: allowed ? row?.controlKeyPublicData.map(ScreenShareControlKey.fingerprint) : nil,
            iceServers: allowed ? await iceServers()?.map(ScreenShareIceServerBody.init) : nil,
            idleTimeoutSeconds: Int(ScreenSharePolicy.idleTimeout(for: grant == .control ? .control : .view)))
        return .success(ScreenShareStateEnvelope(screenShare: body))
    }

    /// The session id a `state` challenge is bound to. A control start moves it
    /// onto the session it opens, so the signature covers the challenge the Mac
    /// actually handed out and cannot be used twice.
    public static func challengeSession(_ deviceId: String) -> String { "pending:" + deviceId }

    /// A fresh challenge, minted only for a phone that may actually control.
    private func challenge(for deviceId: String, allowed: Bool, grant: ScreenShareGrant) async -> String? {
        guard allowed, grant == .control else { return nil }
        return await service.controlChallenge(sessionId: Self.challengeSession(deviceId)).base64EncodedString()
    }

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
        // One question at a time per phone: a phone repeating the request while
        // the dialog is open must not stack a second dialog behind it.
        guard !confirming.contains(deviceId) else { return .failure(.controlKeyPending) }
        confirming.insert(deviceId)
        let fingerprint = ScreenShareControlKey.fingerprint(key)
        let confirmed = await confirmer.confirmControlKey(deviceId: deviceId, fingerprint: fingerprint)
        confirming.remove(deviceId)
        guard confirmed else { return .failure(.controlKeyNotConfirmed) }
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

    /// Admits a session and prepares its first offer. The offer itself goes out
    /// in `sendInitialOffer`, which the route calls once its reply has left, so
    /// the phone never sees an offer for a session id it has not been told.
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
            challenge = await service.adoptChallenge(from: Self.challengeSession(deviceId), to: sessionId)
            guard challenge != nil else { return .failure(.controlSignature) }
        }

        let servers = await iceServers() ?? []
        let turnExpiry = servers.contains { $0.username != nil } ? credential?.expiresAt : nil
        let events = ScreenSharePeerEvents(
            localCandidate: { [weak self] candidate, mid, index, fragment in
                await self?.localCandidate(sessionId: sessionId, candidate: candidate,
                                           sdpMid: mid, sdpMLineIndex: index, usernameFragment: fragment)
            },
            failed: { [weak self] in await self?.end(sessionId: sessionId, reason: .peerLeft) },
            connected: { [weak self] path in await self?.peerConnected(sessionId: sessionId, path: path) },
            data: { [weak self] data in await self?.dataChannel(sessionId: sessionId, data: data) })

        let peer: ScreenSharePeerConnection
        do { peer = try await peers.makePeer(sessionId: sessionId, iceServers: servers, events: events) }
        catch {
            await service.discardChallenge(sessionId: sessionId)
            return .failure(.screenPermission)
        }

        // Capture starts only through the host: the host decides whether frames
        // may flow at all (lock screen, secure input, a kill racing the join),
        // and stops a late capture itself.
        starting[sessionId] = Starting(displayId: requestedDisplay, quality: quality)
        let surface = ScreenShareSurface(
            startCapture: { [weak self] in await self?.hostStartCapture(sessionId: sessionId) },
            stopCapture: { [weak self] in await self?.hostStopCapture() },
            closePeer: { await peer.close() })
        let admitted = await service.join(
            sessionId: sessionId, deviceId: deviceId, mode: mode,
            controlChallenge: challenge, controlSignature: signature, surface: surface)
        let captureFailed = starting.removeValue(forKey: sessionId)?.captureFailed ?? false
        if case .failure(let error) = admitted {
            await peer.close()
            // `join` spends the challenge whether it admits the session or not;
            // this only clears one a refusal never reached.
            await service.discardChallenge(sessionId: sessionId)
            return .failure(Self.refusal(error))
        }

        // Admitted, but frames may not flow: refuse with the reason the phone
        // shows rather than open a session with a frozen picture.
        let refusal: ScreenShareRouteRefusal?
        if captureFailed {
            // Screen Recording missing or expired after an update: the phone
            // asks the person at the Mac rather than showing a black frame.
            refusal = .screenPermission
        } else if framesBlocked {
            refusal = blockReason == .lockScreen ? .lockScreen : .secureInput
        } else {
            refusal = nil
        }
        if let refusal {
            await service.endSession(sessionId: sessionId, reason: .peerLeft)
            await peer.close()
            return .failure(refusal)
        }

        let captured = await capture.displayId ?? requestedDisplay
        sessions[sessionId] = Session(
            deviceId: deviceId, mode: mode, displayId: captured, codec: codec,
            quality: quality, network: network, peer: peer, turnExpiresAt: turnExpiry)
        // A stop the host made between the join and this line (a kill, an idle
        // timer) found no session here to tell; from now on `hostStopped` does.
        guard await service.isLive(sessionId: sessionId) else {
            if forget(sessionId) != nil { await peer.close() }
            return .failure(.sessionStopped)
        }

        let sdp: String
        do { sdp = try await peer.createOffer(codec: codec, quality: quality, iceRestart: false) }
        catch {
            await end(sessionId: sessionId, reason: .peerLeft, notify: false)
            return .failure(.screenPermission)
        }
        // A kill may have taken the session while the offer was being made.
        guard sessions[sessionId] != nil else { return .failure(.sessionStopped) }
        sessions[sessionId]?.pendingOffer = sdp
        sessions[sessionId]?.tasks = [
            renewalTask(sessionId: sessionId),
            connectDeadlineTask(sessionId: sessionId),
        ]
        if codec != .h264 { startLoadWatch() }
        startRefresher()

        return .success(ScreenShareSessionReply(
            sessionId: sessionId, mode: mode.rawValue, displayId: captured,
            codec: codec.rawValue, quality: quality))
    }

    /// Sends the first offer once the route's reply is out, then whatever
    /// candidates were gathered in the meantime, in order.
    public func sendInitialOffer(sessionId: String) async {
        guard let session = sessions[sessionId], let sdp = session.pendingOffer else { return }
        sessions[sessionId]?.pendingOffer = nil
        let queued = session.queuedCandidates
        sessions[sessionId]?.queuedCandidates = []
        await signals.send(.offer(sessionId: sessionId, sdp: sdp, mode: session.mode,
                                  displayId: session.displayId, codec: session.codec,
                                  quality: session.quality, iceRestart: false), to: session.deviceId)
        for candidate in queued { await signals.send(candidate, to: session.deviceId) }
    }

    static func refusal(_ error: ScreenShareError) -> ScreenShareRouteRefusal {
        switch error {
        case .deviceNotAllowed: return .deviceNotAllowed
        case .insufficientGrant: return .insufficientGrant
        case .controlSignatureInvalid: return .controlSignature
        case .concurrencyLimit: return .concurrencyLimit
        case .sessionStopped: return .sessionStopped
        }
    }

    // MARK: Inbound signalling

    /// One `screen-answer`, `screen-ice`, `screen-session-end` or
    /// `screen-background` from a phone. A frame naming a session that phone
    /// does not own is dropped.
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
        case .background(_, let backgrounded):
            setBackground(backgrounded, sessionId: sessionId)
        default:
            // Host-only types: a phone echoing one back is not the Mac.
            break
        }
    }

    private func localCandidate(
        sessionId: String, candidate: String, sdpMid: String?, sdpMLineIndex: Int?, usernameFragment: String?
    ) async {
        guard let session = sessions[sessionId] else { return }
        let signal = ScreenShareSignal.ice(sessionId: sessionId, candidate: candidate, sdpMid: sdpMid,
                                           sdpMLineIndex: sdpMLineIndex, usernameFragment: usernameFragment)
        // Before the first offer the phone has no description to add it to.
        if session.pendingOffer != nil {
            sessions[sessionId]?.queuedCandidates.append(signal)
            return
        }
        await signals.send(signal, to: session.deviceId)
    }

    private func peerConnected(sessionId: String, path: ScreenShareIcePath?) async {
        guard var session = sessions[sessionId] else { return }
        let firstConnect = !session.connected
        session.connected = true
        session.path = path
        // A relayed path has a bandwidth quota, so the sender caps itself to it
        // rather than letting coturn drop packets.
        let capped = ScreenShareQuality.profile(
            network: session.network, path: path ?? .host,
            displayWidth: session.quality.width, displayHeight: session.quality.height)
        let changed = capped.maxBitrateKbps != session.quality.maxBitrateKbps
        session.quality.maxBitrateKbps = capped.maxBitrateKbps
        sessions[sessionId] = session
        if changed { await session.peer.setQuality(session.quality) }
        // A still screen sends nothing new, so the phone that just connected is
        // handed the picture the encoder already has.
        if firstConnect {
            for layer in [ScreenShareCaptureLayer.Kind.primary, .overview] {
                guard var frame = lastFrames[layer] else { continue }
                frame.timestampNanos = frameClock()
                await session.peer.send(frame: frame)
            }
        }
    }

    // MARK: Data channel

    private func dataChannel(sessionId: String, data: Data) async {
        guard sessions[sessionId] != nil else { return }
        switch ScreenShareDataChannel.decode(data) {
        case .failure:
            // A frame the Mac does not understand is dropped in silence; nothing
            // about its contents is logged.
            return
        case .success(let message):
            await apply(message, sessionId: sessionId)
        }
    }

    private func apply(_ message: ScreenShareDataMessage, sessionId: String) async {
        switch message {
        case .input(let event):
            // The host refuses everything a view-only phone sends, everything
            // while the screen is locked or secure input is on, and everything
            // for 2 s after the person at the Mac touched the keyboard.
            _ = await service.deliver(event, sessionId: sessionId)
        case .zoom(let displayId, let region):
            guard mayReshape(sessionId: sessionId), sessions[sessionId]?.displayId == displayId else { return }
            try? await capture.setZoom(region)
            await service.noteActivity(sessionId: sessionId)
        case .display(let displayId):
            guard mayReshape(sessionId: sessionId) else { return }
            await switchDisplay(to: displayId)
            await service.noteActivity(sessionId: sessionId)
        case .clipboard(let frame):
            await acceptClipboard(frame, sessionId: sessionId)
        case .clipboardRequest:
            await sendClipboard(sessionId: sessionId)
        }
    }

    /// One capture serves every phone, so zoom and the display switch change
    /// what everyone sees. A phone may do either when it is alone, or when it
    /// is the one in control; a viewer cannot pull the controller's screen away.
    private func mayReshape(sessionId: String) -> Bool {
        guard let session = sessions[sessionId] else { return false }
        return sessions.count == 1 || session.mode == .control
    }

    private func switchDisplay(to displayId: UInt32) async {
        let captured: UInt32
        do { captured = try await capture.setDisplay(displayId) }
        catch {
            await endAll(reason: .displayGone)
            return
        }
        // A new display means new geometry, so every stream is renegotiated with
        // an ICE restart exactly as the contract says.
        for sessionId in sessions.keys {
            sessions[sessionId]?.displayId = captured
            await renegotiate(sessionId: sessionId, iceRestart: true)
        }
    }

    /// The capture stream stopped on its own — its display was unplugged, or
    /// the system took the stream away. Capture restarts (on the main display
    /// when the old one is gone) and the phones get a fresh offer.
    public func captureInterrupted() async {
        guard !sessions.isEmpty, !framesBlocked, let current = await capture.displayId else { return }
        await switchDisplay(to: current)
    }

    /// The phone's clipboard, reassembled and pasted. Control grant only, and
    /// the host is what enforces that.
    private func acceptClipboard(_ frame: ScreenShareClipboardFrame, sessionId: String) async {
        guard await service.mayInject(sessionId: sessionId), var assembler = sessions[sessionId]?.clipboard else { return }
        let outcome = assembler.accept(frame, now: now())
        sessions[sessionId]?.clipboard = assembler
        guard case .complete(let encoding, let declared, let payload) = outcome else { return }
        guard case .success(let text) = ScreenShareClipboardCodec.decode(
            encoding: encoding, declaredBytes: declared, payload: payload, compressor: compressor)
        else { return }
        // Asked again: the transfer took several frames, and a lock or a kill
        // may have landed between the first and the last.
        guard await service.mayInject(sessionId: sessionId) else { return }
        pasteboard.write(text)
        await service.noteActivity(sessionId: sessionId)
    }

    /// The Mac's clipboard, sent once because the user pressed the button.
    /// A concealed pasteboard item is never read: the phone is told it was
    /// skipped rather than handed an empty paste.
    private func sendClipboard(sessionId: String) async {
        guard await service.mayInject(sessionId: sessionId), let session = sessions[sessionId] else { return }
        let read = pasteboard.read()
        let id = UUID().uuidString
        if read.concealed {
            _ = await session.peer.sendData(Self.encode([
                "t": "clipboard", "dir": "to-phone", "enc": "raw", "bytes": 0,
                "id": id, "seq": 0, "total": 1, "data": "", "concealed": true,
            ]))
            return
        }
        guard let text = read.text,
              let packed = ScreenShareClipboardCodec.encode(text: text, compressor: compressor)
        else { return }
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
    private func setBackground(_ backgrounded: Bool, sessionId: String) {
        guard sessions[sessionId] != nil else { return }
        sessions[sessionId]?.background?.cancel()
        sessions[sessionId]?.background = nil
        guard backgrounded else { return }
        sessions[sessionId]?.background = Task { [weak self, sleeper] in
            try? await sleeper(ScreenSharePolicy.backgroundTimeout)
            guard !Task.isCancelled else { return }
            await self?.end(sessionId: sessionId, reason: .background)
        }
    }

    // MARK: Capture

    /// The host asks for capture when the first session starts, and again once a
    /// lock screen or a password field has cleared.
    private func hostStartCapture(sessionId: String) async {
        let target: (displayId: UInt32, quality: ScreenShareQualityProfile)?
        if let session = sessions[sessionId] {
            target = (session.displayId, session.quality)
        } else if let pending = starting[sessionId] {
            target = (pending.displayId, pending.quality)
        } else if let session = sessions.values.first {
            target = (session.displayId, session.quality)
        } else {
            target = nil
        }
        guard let target else { return }
        do { _ = try await capture.start(displayId: target.displayId, quality: target.quality) }
        catch {
            Self.log.error("screen-share capture did not start: \(String(describing: error), privacy: .public)")
            if starting[sessionId] != nil {
                starting[sessionId]?.captureFailed = true
            } else {
                // Capture was coming back after an unlock and could not: the
                // sessions cannot show anything, so they end and say why. Run
                // outside this call, which the host is waiting on.
                Task { [weak self] in await self?.endAll(reason: .displayGone) }
            }
        }
    }

    private func hostStopCapture() async {
        await capture.stop()
        lastFrames = [:]
    }

    /// One captured frame. Idle frames are dropped before an encoder is woken,
    /// which is what keeps an untouched screen near zero traffic.
    public func deliver(frame: ScreenShareVideoFrame, status: ScreenShareFrameStatus, dirtyRects: Int) async {
        guard !sessions.isEmpty,
              await capture.admit(layer: frame.layer, status: status, dirtyRects: dirtyRects)
        else { return }
        lastFrames[frame.layer] = frame
        lastFrameAt = now()
        for (sessionId, session) in sessions {
            await session.peer.send(frame: frame)
            if frame.layer == .primary { await service.noteFrameDelivered(sessionId: sessionId) }
        }
    }

    /// While the screen stands still nothing is encoded — but once every
    /// keepalive interval the last picture goes out again, so a decoder that
    /// lost a keyframe recovers without anyone touching the Mac.
    private func startRefresher() {
        guard refresher == nil else { return }
        refresher = Task { [weak self, sleeper] in
            while !Task.isCancelled {
                try? await sleeper(ScreenShareFrameGate.keepaliveInterval)
                guard !Task.isCancelled, let self, await self.refreshStillFrame() else { return }
            }
        }
    }

    /// False once there is nothing left to refresh, which ends the loop.
    private func refreshStillFrame() async -> Bool {
        guard !sessions.isEmpty else { refresher = nil; return false }
        guard let last = lastFrameAt, now().timeIntervalSince(last) >= ScreenShareFrameGate.keepaliveInterval,
              var frame = lastFrames[.primary]
        else { return true }
        frame.timestampNanos = frameClock()
        lastFrameAt = now()
        for session in sessions.values where session.connected { await session.peer.send(frame: frame) }
        return true
    }

    // MARK: Codec pressure

    private func startLoadWatch() {
        guard loadWatch == nil else { return }
        loadWatch = Task { [weak self, sleeper] in
            while !Task.isCancelled {
                try? await sleeper(Self.loadCheckInterval)
                guard !Task.isCancelled, let self, await self.checkMachineLoad() else { return }
            }
        }
    }

    /// Once the Mac heats up or runs out of CPU, every software-codec stream
    /// goes back to hardware H.264 with a renegotiation. False when no session
    /// runs a software codec any more, which ends the watch.
    @discardableResult
    public func checkMachineLoad() async -> Bool {
        let conditions = ScreenShareCodecConditions(
            network: .cellular, phoneDecodes: [], cpuHeadroom: load.cpuHeadroom(),
            thermalPressure: load.thermalPressure())
        for (sessionId, session) in sessions
        where ScreenShareCodecPolicy.mustFallBack(to: session.codec, conditions: conditions) {
            sessions[sessionId]?.codec = .h264
            await renegotiate(sessionId: sessionId, iceRestart: false)
        }
        guard sessions.values.contains(where: { $0.codec != .h264 }) else { loadWatch = nil; return false }
        return true
    }

    // MARK: TURN renewal

    private func iceServers() async -> [ScreenShareIceServer]? {
        if let credential, !credential.needsRenewal(now: now()) { return credential.iceServers }
        guard let fresh = await turn.mintTurnCredential() else {
            // An expired credential is worth nothing to the phone.
            guard let credential, !credential.isExpired(now: now()) else { return nil }
            return credential.iceServers
        }
        credential = fresh
        return fresh.iceServers
    }

    /// Waits until the session's credential is close to expiry, then renews it.
    /// A session without TURN has nothing to renew.
    private func renewalTask(sessionId: String) -> Task<Void, Never> {
        Task { [weak self, sleeper] in
            while !Task.isCancelled {
                guard let delay = await self?.renewalDelay(sessionId: sessionId) else { return }
                try? await sleeper(delay)
                guard !Task.isCancelled else { return }
                await self?.renewCredential(sessionId: sessionId)
            }
        }
    }

    private func renewalDelay(sessionId: String) -> TimeInterval? {
        guard let session = sessions[sessionId], let expiry = session.turnExpiresAt,
              now() < expiry else { return nil }
        let due = expiry.addingTimeInterval(-ScreenShareTurnCredential.renewalMargin)
        let wait = due.timeIntervalSince(now())
        // Past due means the last attempt failed: retry, but not in a tight loop.
        return wait > 0 ? wait : Self.renewalRetry
    }

    /// Mints a fresh credential (or reuses one another session just minted),
    /// hands it to the peer, tells the phone in a `screen-grant`, then
    /// renegotiates with an ICE restart — with the renewed servers already on
    /// the peer, so the restart gathers against them rather than the pair about
    /// to expire.
    func renewCredential(sessionId: String) async {
        guard let session = sessions[sessionId] else { return }
        let current = session.turnExpiresAt ?? .distantPast
        // Another session may already have renewed; its credential is reused.
        var fresh = credential
        let reusable = fresh.map { $0.expiresAt > current && !$0.needsRenewal(now: now()) } ?? false
        if !reusable {
            fresh = await turn.mintTurnCredential()
            if let fresh { credential = fresh }
        }
        guard let fresh, fresh.expiresAt > current, sessions[sessionId] != nil else { return }
        sessions[sessionId]?.turnExpiresAt = fresh.expiresAt
        let servers = fresh.iceServers
        await session.peer.setConfiguration(iceServers: servers)
        await signals.send(await grantSignal(deviceId: session.deviceId, sessionId: sessionId, iceServers: servers),
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
        guard let current = sessions[sessionId] else { return }
        // The first offer has not left yet: this one replaces it.
        if current.pendingOffer != nil { sessions[sessionId]?.pendingOffer = sdp; return }
        await signals.send(.offer(sessionId: sessionId, sdp: sdp, mode: current.mode,
                                  displayId: current.displayId, codec: current.codec,
                                  quality: current.quality, iceRestart: iceRestart),
                           to: current.deviceId)
    }

    /// Gives up on a peer that never connected.
    private func connectDeadlineTask(sessionId: String) -> Task<Void, Never> {
        Task { [weak self, sleeper] in
            try? await sleeper(Self.connectDeadline)
            guard !Task.isCancelled, let self else { return }
            await self.connectDeadlineReached(sessionId: sessionId)
        }
    }

    private func connectDeadlineReached(sessionId: String) async {
        guard let session = sessions[sessionId], !session.connected else { return }
        await end(sessionId: sessionId, reason: .peerLeft)
    }

    // MARK: Stopping

    /// Ends one session on the Mac and tells the phone.
    public func end(sessionId: String, reason: ScreenShareStopReason) async {
        await end(sessionId: sessionId, reason: reason, notify: true)
    }

    private func end(sessionId: String, reason: ScreenShareStopReason, notify: Bool) async {
        guard let session = forget(sessionId) else { return }
        await service.endSession(sessionId: sessionId, reason: reason)
        await session.peer.close()
        guard notify else { return }
        await signals.send(Self.stopSignal(sessionId: sessionId, reason: reason), to: session.deviceId)
    }

    private func endAll(reason: ScreenShareStopReason) async {
        for sessionId in sessions.keys { await end(sessionId: sessionId, reason: reason) }
    }

    /// Drops the engine's own record and timers of a session.
    private func forget(_ sessionId: String) -> Session? {
        guard let session = sessions.removeValue(forKey: sessionId) else { return nil }
        session.tasks.forEach { $0.cancel() }
        session.background?.cancel()
        if sessions.isEmpty {
            refresher?.cancel(); refresher = nil
            loadWatch?.cancel(); loadWatch = nil
            lastFrames = [:]
        }
        return session
    }

    static func stopSignal(sessionId: String, reason: ScreenShareStopReason) -> ScreenShareSignal {
        if let kill = ScreenShareWireReason.kill(reason) { return .kill(sessionId: sessionId, reason: kill) }
        return .sessionEnd(sessionId: sessionId, reason: ScreenShareWireReason.sessionEnd(reason))
    }

    /// Called after the host stopped sessions on its own (kill switch, revoke,
    /// downgrade, rekey, idle timeout). The host has already stopped capture,
    /// injection and the peers; this drops the engine's own bookkeeping and
    /// sends the courtesy note.
    public func hostStopped(_ stopped: [ScreenShareStoppedSession]) async {
        for entry in stopped {
            guard let session = forget(entry.sessionId) else { continue }
            await session.peer.close()
            await signals.send(Self.stopSignal(sessionId: entry.sessionId, reason: entry.reason), to: entry.deviceId)
        }
    }

    /// The lock screen came on, or a password field took focus. The host has
    /// already stopped the frames; the sessions go too, so their slots are free
    /// and the phone is told why rather than watching a frozen picture.
    public func framesBlockedChanged(_ blocked: Bool, reason: ScreenShareStopReason) async {
        if blocked { blockReason = reason }
        guard blocked != framesBlocked else { return }
        framesBlocked = blocked
        guard blocked else { return }
        await endAll(reason: reason)
    }

    /// Settings changed for a phone: it learns its new allow-list and grant
    /// state even when it has no session open.
    public func pushGrant(to deviceId: String) async {
        let row = await service.settings(for: deviceId)
        let servers = row?.allowed == true ? await iceServers() : nil
        await signals.send(await grantSignal(deviceId: deviceId, sessionId: nil, iceServers: servers),
                           to: deviceId)
    }

    /// One `screen-grant` with the phone's real allow-list and grant — never a
    /// session's mode in its place — and a fresh challenge when it may control.
    private func grantSignal(deviceId: String, sessionId: String?, iceServers: [ScreenShareIceServer]?) async -> ScreenShareSignal {
        let row = await service.settings(for: deviceId)
        let grant = row?.grant ?? .none
        let allowed = row?.allowed == true
        return .grant(sessionId: sessionId, allowed: allowed, grant: grant,
                      controlChallengeB64: await challenge(for: deviceId, allowed: allowed, grant: grant),
                      iceServers: allowed ? iceServers : nil,
                      displays: displays.info())
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
