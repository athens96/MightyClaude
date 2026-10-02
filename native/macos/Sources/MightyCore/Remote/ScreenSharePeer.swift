import Foundation

/// What `getStats` on the host side is good for: the phone computes its own
/// numbers, and these are what the Mac needs to adapt quality and to say which
/// path the video actually took.
public struct ScreenSharePeerStats: Sendable, Equatable {
    public var selectedPath: ScreenShareIcePath?
    public var roundTripMs: Double?
    public var outgoingBitrateKbps: Double?
    public var framesEncoded: Int?

    public init(selectedPath: ScreenShareIcePath? = nil, roundTripMs: Double? = nil,
                outgoingBitrateKbps: Double? = nil, framesEncoded: Int? = nil) {
        self.selectedPath = selectedPath; self.roundTripMs = roundTripMs
        self.outgoingBitrateKbps = outgoingBitrateKbps; self.framesEncoded = framesEncoded
    }
}

/// How a peer connection reports back. Held by the engine, which owns the
/// session, so the peer never has to know the safety rules.
public struct ScreenSharePeerEvents: Sendable {
    /// One locally gathered ICE candidate, trickled out as it appears. An empty
    /// candidate string is the documented end-of-candidates marker.
    public var localCandidate: @Sendable (String, String?, Int?, String?) async -> Void
    /// The peer failed or went away for good.
    public var failed: @Sendable () async -> Void
    /// The peer is connected and `selectedPath` is known.
    public var connected: @Sendable (ScreenShareIcePath?) async -> Void
    /// One data-channel frame from the phone.
    public var data: @Sendable (Data) async -> Void

    public init(
        localCandidate: @escaping @Sendable (String, String?, Int?, String?) async -> Void,
        failed: @escaping @Sendable () async -> Void,
        connected: @escaping @Sendable (ScreenShareIcePath?) async -> Void,
        data: @escaping @Sendable (Data) async -> Void
    ) {
        self.localCandidate = localCandidate; self.failed = failed
        self.connected = connected; self.data = data
    }
}

/// How the video and the data channel are negotiated. Implemented over the
/// bundled libwebrtc in the app; a fake peer satisfies it in tests, so the whole
/// session lifecycle — offer, answer, trickle, renewal, teardown — runs without
/// a network or a display.
public protocol ScreenSharePeerConnection: Sendable, AnyObject {
    /// Replaces the ICE servers on a live peer. Called with freshly minted TURN
    /// credentials just before the ICE restart that puts them to use, so the
    /// restart gathers against the new ones rather than the expiring pair.
    func setConfiguration(iceServers: [ScreenShareIceServer]) async

    /// The Mac sends the video, so the Mac makes the offer. `contentHint` and
    /// `maintain-resolution` are applied here: screen text must stay readable
    /// before the frame rate is defended.
    func createOffer(
        codec: ScreenShareVideoCodec, quality: ScreenShareQualityProfile, iceRestart: Bool
    ) async throws -> String

    /// Applies a new bitrate and frame-rate ceiling to the live sender — a
    /// relayed path drops to the TURN quota — without a renegotiation.
    func setQuality(_ quality: ScreenShareQualityProfile) async

    func acceptAnswer(_ sdp: String) async throws
    func addRemoteCandidate(candidate: String, sdpMid: String?, sdpMLineIndex: Int?) async

    /// One captured frame for the encoder. A `primary` frame goes on the first
    /// video track (stream id `screen`), an `overview` frame on the second
    /// (stream id `overview`), which carries nothing while not zoomed.
    func send(frame: ScreenShareVideoFrame) async

    /// One data-channel message. False when the channel is not open or the
    /// message did not leave, so the caller never reports a paste as delivered
    /// that never left the Mac.
    func sendData(_ data: Data) async -> Bool

    func close() async
    func statistics() async -> ScreenSharePeerStats
}

/// Makes peer connections. The engine asks for one per session.
public protocol ScreenSharePeerFactory: Sendable {
    func makePeer(
        sessionId: String, iceServers: [ScreenShareIceServer], events: ScreenSharePeerEvents
    ) async throws -> ScreenSharePeerConnection
}

/// Sends one signalling frame to a paired phone inside the existing E2EE
/// channel. False when that phone has no live connection: a kill must never wait
/// on the relay, so the host stops first and tells the phone if it can.
public protocol ScreenShareSignalSender: Sendable {
    @discardableResult
    func send(_ signal: ScreenShareSignal, to deviceId: String) async -> Bool
}

/// Asks the relay to mint a short-lived TURN credential. Nil when the relay is
/// unreachable, rate-limited the request, or has no coturn secret: the session
/// then runs on host and reflexive candidates only, which is the common case on
/// the same Wi-Fi anyway.
public protocol ScreenShareTurnSource: Sendable {
    func mintTurnCredential() async -> ScreenShareTurnCredential?
}

/// Reads the Mac's own pressure, so a software codec is only chosen while there
/// is room for it.
public protocol ScreenShareMachineLoad: Sendable {
    /// Share of CPU still free, 0…1.
    func cpuHeadroom() -> Double
    func thermalPressure() -> Bool
}

/// The default: `ProcessInfo`'s thermal state plus the load average against the
/// core count.
public struct SystemScreenShareLoad: ScreenShareMachineLoad {
    public init() {}

    public func cpuHeadroom() -> Double {
        var average = [Double](repeating: 0, count: 1)
        guard getloadavg(&average, 1) == 1 else { return 0 }
        let cores = Double(max(1, ProcessInfo.processInfo.activeProcessorCount))
        return max(0, min(1, 1 - average[0] / cores))
    }

    public func thermalPressure() -> Bool {
        switch ProcessInfo.processInfo.thermalState {
        case .serious, .critical: return true
        default: return false
        }
    }
}
