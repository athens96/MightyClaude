import Foundation
import MightyCore
import WebRTC

/// The bundled libwebrtc peer connection the Mac sends the screen over.
///
/// The Mac is the sender, so the Mac makes the offer. Three settings carry the
/// "terminal text stays readable" rule into the encoder:
///   • both video sources are screencast sources (`videoSource(forScreenCast:)`)
///     — inside libwebrtc that is what the `text`/`detail` content hint turns
///     into, and the Objective-C API has no separate `contentHint`;
///   • `degradationPreference = maintainResolution`, so a tight link drops the
///     frame rate rather than blurring the glyphs;
///   • an explicit codec preference: hardware (VideoToolbox) H.264 first unless
///     the codec policy chose VP9 or AV1. HEVC is never offered.
///
/// Two send-only video tracks: `screen` carries the primary layer (the whole
/// display, or the zoomed region at the full pixel budget) and `overview` the
/// low-resolution whole display under a zoom — it is silent while not zoomed.
/// The data channel `screen-control` carries input and the clipboard.
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
    /// The overview never needs more than this; it is a 2 fps thumbnail.
    static let overviewBitrateKbps = 150

    private struct Layer {
        let source: RTCVideoSource
        let capturer: RTCVideoCapturer
        let track: RTCVideoTrack
        let transceiver: RTCRtpTransceiver
    }

    private let lock = NSLock()
    private let events: ScreenSharePeerEvents
    private let connection: RTCPeerConnection
    private var layers: [ScreenShareCaptureLayer.Kind: Layer] = [:]
    private var dataChannel: RTCDataChannel?
    private var closed = false
    private var selected: ScreenShareIcePath?
    private var quality: ScreenShareQualityProfile?
    /// Connection callbacks (candidates, state changes) arrive on WebRTC's
    /// threads and reach the engine one at a time, in order. There are only a
    /// handful per connection.
    private let pump: AsyncStream<@Sendable () async -> Void>.Continuation
    private let pumpTask: Task<Void, Never>
    /// Data-channel messages reach the engine one at a time and in order, so a
    /// drag's begin is never overtaken by its end — through a bounded queue: a
    /// phone that floods the channel faster than the Mac handles it loses the
    /// newest messages, counted, instead of growing the Mac's memory.
    private let inbox: AsyncStream<Data>.Continuation
    private let inboxTask: Task<Void, Never>
    static let inboxCapacity = 256
    private var droppedMessages = 0

    init(sessionId: String, iceServers: [ScreenShareIceServer], events: ScreenSharePeerEvents) throws {
        self.events = events
        let (jobs, pump) = AsyncStream.makeStream(of: (@Sendable () async -> Void).self)
        self.pump = pump
        pumpTask = Task.detached(priority: .userInitiated) { for await job in jobs { await job() } }
        let (messages, inbox) = AsyncStream.makeStream(
            of: Data.self, bufferingPolicy: .bufferingOldest(Self.inboxCapacity))
        self.inbox = inbox
        let deliver = events.data
        inboxTask = Task.detached(priority: .userInitiated) { for await message in messages { await deliver(message) } }
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
        super.init()
        connection.delegate = self

        for (kind, streamId) in [(ScreenShareCaptureLayer.Kind.primary, "screen"), (.overview, "overview")] {
            let source = Self.factory.videoSource(forScreenCast: true)
            let track = Self.factory.videoTrack(with: source, trackId: streamId + "-" + sessionId)
            let transceiverInit = RTCRtpTransceiverInit()
            transceiverInit.direction = .sendOnly
            transceiverInit.streamIds = [streamId]
            guard let transceiver = connection.addTransceiver(with: track, init: transceiverInit) else {
                throw ScreenShareCaptureError.backendFailure
            }
            layers[kind] = Layer(source: source, capturer: RTCVideoCapturer(delegate: source),
                                 track: track, transceiver: transceiver)
        }
        // The data channel carries input and the clipboard. The Mac opens it so
        // it exists before the phone needs it; ordered and reliable, because a
        // dropped keystroke is worse than a late one.
        let channelConfiguration = RTCDataChannelConfiguration()
        channelConfiguration.isOrdered = true
        dataChannel = connection.dataChannel(forLabel: "screen-control", configuration: channelConfiguration)
        dataChannel?.delegate = self
    }

    deinit {
        connection.close()
        pump.finish()
        inbox.finish()
    }

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

    func setQuality(_ quality: ScreenShareQualityProfile) async {
        lock.lock(); self.quality = quality; lock.unlock()
        applyQuality()
    }

    /// Bitrate and frame-rate ceilings on both senders, and the screen-content
    /// degradation preference: keep the pixels, spend the frame rate.
    private func applyQuality() {
        lock.lock(); let quality = self.quality; lock.unlock()
        guard let quality else { return }
        for (kind, layer) in layers {
            let sender = layer.transceiver.sender
            let parameters = sender.parameters
            for encoding in parameters.encodings {
                let kbps = kind == .primary ? quality.maxBitrateKbps
                    : min(Self.overviewBitrateKbps, quality.maxBitrateKbps)
                encoding.maxBitrateBps = NSNumber(value: kbps * 1_000)
                encoding.maxFramerate = NSNumber(value: kind == .primary
                    ? quality.fps : ScreenShareCapturePlanner.overviewFps)
            }
            parameters.degradationPreference = NSNumber(value: RTCDegradationPreference.maintainResolution.rawValue)
            sender.parameters = parameters
        }
    }

    /// Puts the chosen codec first on both video transceivers, keeping the
    /// retransmission and FEC entries behind it. HEVC is never in the list.
    private func preferCodec(_ codec: ScreenShareVideoCodec) {
        let capabilities = Self.factory.rtpSenderCapabilities(forKind: kRTCMediaStreamTrackKindVideo)
        let usable = capabilities.codecs.filter { !["H265", "HEVC"].contains($0.name.uppercased()) }
        let wanted = codec.rawValue.uppercased()
        let ordered = usable.filter { $0.name.uppercased() == wanted } + usable.filter { $0.name.uppercased() != wanted }
        guard !ordered.isEmpty else { return }
        for layer in layers.values {
            if let error = Self.setCodecPreferences(ordered, on: layer.transceiver) {
                NSLog("screen-share codec preference refused: %@", error.localizedDescription)
            }
        }
    }

    /// `setCodecPreferences:error:` called through its selector. Swift imports
    /// it under the same name as the deprecated `setCodecPreferences:`, so the
    /// compiler always picks the deprecated one, which drops a refusal on the
    /// floor. Returns the refusal, or nil when the preference took.
    private static func setCodecPreferences(_ codecs: [RTCRtpCodecCapability], on transceiver: RTCRtpTransceiver) -> NSError? {
        typealias Setter = @convention(c) (
            AnyObject, Selector, NSArray, AutoreleasingUnsafeMutablePointer<NSError?>?
        ) -> Bool
        let selector = NSSelectorFromString("setCodecPreferences:error:")
        let object: NSObject = transceiver
        guard object.responds(to: selector) else {
            return NSError(domain: "dev.mightyclaude.screen-share", code: 1)
        }
        let setter = unsafeBitCast(object.method(for: selector), to: Setter.self)
        var error: NSError?
        let accepted = setter(object, selector, codecs as NSArray, &error)
        return accepted ? nil : (error ?? NSError(domain: "dev.mightyclaude.screen-share", code: 2))
    }

    // MARK: Offer / answer

    func createOffer(
        codec: ScreenShareVideoCodec, quality: ScreenShareQualityProfile, iceRestart: Bool
    ) async throws -> String {
        lock.lock(); self.quality = quality; lock.unlock()
        preferCodec(codec)
        let constraints = RTCMediaConstraints(
            mandatoryConstraints: iceRestart ? ["IceRestart": "true"] : [:],
            optionalConstraints: nil)
        let offer = try await connection.offer(for: constraints)
        try await connection.setLocalDescription(offer)
        // Encodings exist once the local description does.
        applyQuality()
        return offer.sdp
    }

    func acceptAnswer(_ sdp: String) async throws {
        try await connection.setRemoteDescription(RTCSessionDescription(type: .answer, sdp: sdp))
        applyQuality()
    }

    func addRemoteCandidate(candidate: String, sdpMid: String?, sdpMLineIndex: Int?) async {
        // An empty candidate is the end-of-candidates marker, not a candidate.
        guard !candidate.isEmpty else { return }
        let ice = RTCIceCandidate(sdp: candidate, sdpMLineIndex: Int32(sdpMLineIndex ?? 0), sdpMid: sdpMid)
        try? await connection.add(ice)
    }

    // MARK: Frames

    func send(frame: ScreenShareVideoFrame) async {
        guard let layer = layers[frame.layer] else { return }
        let buffer = RTCCVPixelBuffer(pixelBuffer: frame.pixelBuffer)
        let video = RTCVideoFrame(buffer: buffer, rotation: ._0, timeStampNs: frame.timestampNanos)
        layer.source.capturer(layer.capturer, didCapture: video)
    }

    func sendData(_ data: Data) async -> Bool {
        lock.lock(); let channel = dataChannel; lock.unlock()
        guard let channel, channel.readyState == .open else { return false }
        return channel.sendData(RTCDataBuffer(data: data, isBinary: false))
    }

    func close() async {
        lock.lock()
        guard !closed else { lock.unlock(); return }
        closed = true
        let channel = dataChannel
        lock.unlock()
        channel?.close()
        connection.close()
    }

    func statistics() async -> ScreenSharePeerStats {
        let report = await connection.statistics()
        var stats = ScreenSharePeerStats(selectedPath: currentPath())
        for value in report.statistics.values {
            if value.type == "candidate-pair", value.values["state"] as? String == "succeeded",
               let rtt = value.values["currentRoundTripTime"] as? NSNumber {
                stats.roundTripMs = rtt.doubleValue * 1_000
            }
            if value.type == "outbound-rtp", value.values["kind"] as? String == "video",
               let frames = (value.values["framesEncoded"] as? NSNumber)?.intValue {
                stats.framesEncoded = (stats.framesEncoded ?? 0) + frames
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
        pump.yield { await events.localCandidate(sdp, mid, index, nil) }
    }

    func peerConnection(_ peerConnection: RTCPeerConnection, didChange newState: RTCIceConnectionState) {
        let events = events
        switch newState {
        case .connected, .completed:
            pump.yield { [weak self] in
                let path = await self?.resolveSelectedPath()
                await events.connected(path)
            }
        case .failed, .closed:
            // `disconnected` is often transient (a Wi-Fi hand-over); ICE either
            // recovers or reaches `failed` on its own.
            pump.yield { await events.failed() }
        default:
            break
        }
    }

    /// The candidate pair that won, so the quality cap can respect the TURN
    /// quota on a relayed path.
    private func resolveSelectedPath() async -> ScreenShareIcePath? {
        let report = await connection.statistics()
        let pairs = report.statistics.values.filter {
            $0.type == "candidate-pair" && $0.values["state"] as? String == "succeeded"
        }
        let pair = pairs.first { $0.values["nominated"] as? Bool == true } ?? pairs.first
        guard let localId = pair?.values["localCandidateId"] as? String,
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
        pump.yield { await events.localCandidate("", nil, nil, nil) }
    }
    func peerConnection(_ peerConnection: RTCPeerConnection, didRemove candidates: [RTCIceCandidate]) {}
}

extension WebRTCScreenSharePeer: RTCDataChannelDelegate {
    func dataChannelDidChangeState(_ dataChannel: RTCDataChannel) {}

    func dataChannel(_ dataChannel: RTCDataChannel, didReceiveMessageWith buffer: RTCDataBuffer) {
        // Larger than any message the contract allows: never queued at all.
        guard buffer.data.count <= ScreenShareDataChannel.maximumMessageBytes else { return }
        if case .dropped = inbox.yield(buffer.data) {
            lock.lock(); droppedMessages += 1; let dropped = droppedMessages; lock.unlock()
            // Once, then every 256th: the count, never the contents.
            if dropped == 1 || dropped % Self.inboxCapacity == 0 {
                NSLog("screen-share data channel full: %d messages dropped", dropped)
            }
        }
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
