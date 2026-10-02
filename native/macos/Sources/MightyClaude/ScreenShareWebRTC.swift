import Foundation
import MightyCore
import WebRTC

/// The bundled libwebrtc peer connection the Mac sends the screen over.
///
/// The Mac is the sender, so the Mac makes the offer. Three settings carry the
/// "terminal text stays readable" rule into the encoder:
///   • the video source is created `forScreenCast`, which is the native SDK's
///     form of the `detail`/`text` content hint — the web API's `contentHint` has
///     no ObjC property, and this is the flag it maps onto;
///   • `degradationPreference = .maintainResolution`, so a tight link drops the
///     frame rate rather than blurring the glyphs;
///   • an explicit codec preference, with hardware H.264 first unless the codec
///     policy chose otherwise.
///
/// Video never touches the relay: candidates are trickled out through the E2EE
/// signalling channel, and a same-Wi-Fi pair connects on host candidates alone.
final class WebRTCScreenSharePeer: NSObject, ScreenSharePeerConnection, @unchecked Sendable {
    private static let factory: RTCPeerConnectionFactory = {
        RTCInitializeSSL()
        return RTCPeerConnectionFactory(
            encoderFactory: RTCDefaultVideoEncoderFactory(),
            decoderFactory: RTCDefaultVideoDecoderFactory())
    }()

    private let lock = NSLock()
    private let events: ScreenSharePeerEvents
    private let connection: RTCPeerConnection
    private let source: RTCVideoSource
    private let track: RTCVideoTrack
    private var sender: RTCRtpSender?
    /// One capturer for the life of the peer: `RTCVideoSource` wants the same
    /// sender object on every frame, not a fresh one per frame.
    private let capturer: RTCVideoCapturer
    private var dataChannel: RTCDataChannel?
    private var closed = false
    private var selected: ScreenShareIcePath?

    init(sessionId: String, iceServers: [ScreenShareIceServer], events: ScreenSharePeerEvents) throws {
        self.events = events
        let configuration = RTCConfiguration()
        configuration.iceServers = Self.servers(iceServers)
        configuration.sdpSemantics = .unifiedPlan
        configuration.continualGatheringPolicy = .gatherContinually
        configuration.bundlePolicy = .maxBundle
        configuration.rtcpMuxPolicy = .require
        let constraints = RTCMediaConstraints(mandatoryConstraints: nil, optionalConstraints: nil)
        guard let connection = Self.factory.peerConnection(
            with: configuration, constraints: constraints, delegate: nil)
        else { throw ScreenShareCaptureError.backendFailure }
        self.connection = connection
        source = Self.factory.videoSource(forScreenCast: true)
        track = Self.factory.videoTrack(with: source, trackId: "screen-" + sessionId)
        capturer = RTCVideoCapturer(delegate: source)
        super.init()
        connection.delegate = self

        sender = connection.add(track, streamIds: ["screen"])
        // The data channel carries input and the clipboard. The Mac opens it so
        // it exists before the phone needs it; it is ordered and reliable because
        // a dropped keystroke is worse than a late one.
        let channelConfiguration = RTCDataChannelConfiguration()
        channelConfiguration.isOrdered = true
        dataChannel = connection.dataChannel(forLabel: "screen-control", configuration: channelConfiguration)
        dataChannel?.delegate = self
        applyScreenContentTuning()
    }

    deinit { connection.close() }

    // MARK: Configuration

    private static func servers(_ servers: [ScreenShareIceServer]) -> [RTCIceServer] {
        servers.map { server in
            if let username = server.username, let credential = server.credential {
                return RTCIceServer(urlStrings: server.urls, username: username, credential: credential)
            }
            return RTCIceServer(urlStrings: server.urls)
        }
    }

    /// Replaces the ICE servers on the live peer, so a following ICE restart
    /// gathers against the renewed TURN credential rather than the expiring one.
    func setConfiguration(iceServers: [ScreenShareIceServer]) async {
        let configuration = connection.configuration
        configuration.iceServers = Self.servers(iceServers)
        _ = connection.setConfiguration(configuration)
    }

    /// Screen content, not video: keep the pixels, spend the frame rate.
    private func applyScreenContentTuning() {
        guard let sender else { return }
        let parameters = sender.parameters
        parameters.degradationPreference = NSNumber(value: RTCDegradationPreference.maintainResolution.rawValue)
        sender.parameters = parameters
    }

    private func applyBitrate(_ quality: ScreenShareQualityProfile) {
        guard let sender else { return }
        let parameters = sender.parameters
        for encoding in parameters.encodings {
            encoding.maxBitrateBps = NSNumber(value: quality.maxBitrateKbps * 1_000)
            encoding.maxFramerate = NSNumber(value: quality.fps)
        }
        parameters.degradationPreference = NSNumber(value: RTCDegradationPreference.maintainResolution.rawValue)
        sender.parameters = parameters
    }

    /// Puts the chosen codec first on the video transceiver. HEVC is never in
    /// the list, so it cannot be negotiated by accident.
    private func preferCodec(_ codec: ScreenShareVideoCodec) {
        guard let transceiver = connection.transceivers.first(where: { $0.mediaType == .video }) else { return }
        let capabilities = Self.factory.rtpSenderCapabilities(forKind: kRTCMediaStreamTrackKindVideo)
        let wanted = codec.rawValue.uppercased()
        let ordered = capabilities.codecs.filter { $0.name.uppercased() != "H265" }
            .sorted { left, _ in left.name.uppercased() == wanted }
        guard !ordered.isEmpty else { return }
        // The SDK ships two overloads of this selector and both import into Swift
        // under the same name; a non-throwing function is a subtype of a throwing
        // one, so Swift always resolves to the deprecated void form and the
        // `error:` variant is unreachable from Swift. The call is the same either
        // way — the only thing lost is a failure report, and a codec preference
        // the SDK rejects simply leaves the SDP's default order in place.
        transceiver.setCodecPreferences(ordered)
    }

    // MARK: Offer / answer

    func createOffer(
        codec: ScreenShareVideoCodec, quality: ScreenShareQualityProfile, iceRestart: Bool
    ) async throws -> String {
        preferCodec(codec)
        let constraints = RTCMediaConstraints(
            mandatoryConstraints: iceRestart ? ["IceRestart": "true"] : [:],
            optionalConstraints: nil)
        let offer = try await connection.offer(for: constraints)
        try await connection.setLocalDescription(offer)
        applyBitrate(quality)
        return offer.sdp
    }

    func acceptAnswer(_ sdp: String) async throws {
        try await connection.setRemoteDescription(RTCSessionDescription(type: .answer, sdp: sdp))
    }

    func addRemoteCandidate(candidate: String, sdpMid: String?, sdpMLineIndex: Int?) async {
        // An empty candidate is the end-of-candidates marker, not a candidate.
        guard !candidate.isEmpty else { return }
        let ice = RTCIceCandidate(sdp: candidate, sdpMLineIndex: Int32(sdpMLineIndex ?? 0), sdpMid: sdpMid)
        try? await connection.add(ice)
    }

    // MARK: Frames

    func send(frame: ScreenShareVideoFrame) async {
        // Only the primary layer goes down the wire as video; the overview layer
        // is composited by the phone under the zoomed region.
        let buffer = RTCCVPixelBuffer(pixelBuffer: frame.pixelBuffer)
        let video = RTCVideoFrame(buffer: buffer, rotation: ._0, timeStampNs: frame.timestampNanos)
        source.capturer(capturer, didCapture: video)
    }

    func sendData(_ data: Data) async -> Bool {
        guard let channel = dataChannel, channel.readyState == .open else { return false }
        return channel.sendData(RTCDataBuffer(data: data, isBinary: false))
    }

    func close() async {
        lock.lock()
        guard !closed else { lock.unlock(); return }
        closed = true
        lock.unlock()
        dataChannel?.close()
        connection.close()
    }

    func statistics() async -> ScreenSharePeerStats {
        let report = await connection.statistics()
        var stats = ScreenSharePeerStats(selectedPath: currentPath())
        for value in report.statistics.values {
            if value.type == "candidate-pair", value.values["state"] as? String == "succeeded" {
                if let rtt = value.values["currentRoundTripTime"] as? NSNumber {
                    stats.roundTripMs = rtt.doubleValue * 1_000
                }
            }
            if value.type == "outbound-rtp", value.values["kind"] as? String == "video" {
                stats.framesEncoded = (value.values["framesEncoded"] as? NSNumber)?.intValue
            }
        }
        return stats
    }

    private func currentPath() -> ScreenShareIcePath? {
        lock.lock(); defer { lock.unlock() }
        return selected
    }
}

// MARK: - Delegates

extension WebRTCScreenSharePeer: RTCPeerConnectionDelegate {
    func peerConnection(_ peerConnection: RTCPeerConnection, didGenerate candidate: RTCIceCandidate) {
        let events = events
        let sdp = candidate.sdp
        let mid = candidate.sdpMid
        let index = Int(candidate.sdpMLineIndex)
        Task { await events.localCandidate(sdp, mid, index, nil) }
    }

    func peerConnection(_ peerConnection: RTCPeerConnection, didChange newState: RTCIceConnectionState) {
        let events = events
        switch newState {
        case .connected, .completed:
            Task { [weak self] in
                let path = await self?.resolveSelectedPath()
                await events.connected(path)
            }
        case .failed, .closed, .disconnected:
            if newState != .disconnected { Task { await events.failed() } }
        default:
            break
        }
    }

    /// The candidate pair that won, so the quality cap can respect the TURN
    /// quota on a relayed path.
    private func resolveSelectedPath() async -> ScreenShareIcePath? {
        let report = await connection.statistics()
        guard let pair = report.statistics.values.first(where: {
            $0.type == "candidate-pair" && $0.values["state"] as? String == "succeeded"
        }), let localId = pair.values["localCandidateId"] as? String,
              let local = report.statistics[localId],
              let kind = local.values["candidateType"] as? String
        else { return nil }
        let path = ScreenShareIcePath(rawValue: kind)
        lock.lock(); selected = path; lock.unlock()
        return path
    }

    func peerConnection(_ peerConnection: RTCPeerConnection, didOpen dataChannel: RTCDataChannel) {
        dataChannel.delegate = self
        lock.lock(); self.dataChannel = dataChannel; lock.unlock()
    }

    func peerConnectionShouldNegotiate(_ peerConnection: RTCPeerConnection) {}
    func peerConnection(_ peerConnection: RTCPeerConnection, didChange stateChanged: RTCSignalingState) {}
    func peerConnection(_ peerConnection: RTCPeerConnection, didAdd stream: RTCMediaStream) {}
    func peerConnection(_ peerConnection: RTCPeerConnection, didRemove stream: RTCMediaStream) {}
    func peerConnection(_ peerConnection: RTCPeerConnection, didChange newState: RTCIceGatheringState) {
        guard newState == .complete else { return }
        let events = events
        // The documented end-of-candidates marker.
        Task { await events.localCandidate("", nil, nil, nil) }
    }
    func peerConnection(_ peerConnection: RTCPeerConnection, didRemove candidates: [RTCIceCandidate]) {}
}

extension WebRTCScreenSharePeer: RTCDataChannelDelegate {
    func dataChannelDidChangeState(_ dataChannel: RTCDataChannel) {}

    func dataChannel(_ dataChannel: RTCDataChannel, didReceiveMessageWith buffer: RTCDataBuffer) {
        let events = events
        let data = buffer.data
        Task { await events.data(data) }
    }
}

/// Makes one peer per session.
struct WebRTCScreenSharePeerFactory: ScreenSharePeerFactory {
    func makePeer(
        sessionId: String, iceServers: [ScreenShareIceServer], events: ScreenSharePeerEvents
    ) async throws -> ScreenSharePeerConnection {
        try WebRTCScreenSharePeer(sessionId: sessionId, iceServers: iceServers, events: events)
    }
}
