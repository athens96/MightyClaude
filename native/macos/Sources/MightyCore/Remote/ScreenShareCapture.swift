import CoreGraphics
import CoreVideo
import Foundation

// MARK: - Idle frames

/// What ScreenCaptureKit said about one delivered frame. Mirrors `SCFrameStatus`
/// so the gate below can be exercised without a display.
public enum ScreenShareFrameStatus: String, Sendable, Equatable {
    case complete, idle, blank, suspended, started, stopped
}

/// Drops the frames nobody needs, which is what makes an untouched screen cost
/// almost nothing. ScreenCaptureKit already behaves at zero hertz — it stops
/// delivering while nothing changes — and this is the second line: a frame that
/// arrives with no dirty rectangle carries no new pixels and is not encoded.
public struct ScreenShareFrameGate: Sendable, Equatable {
    /// A still screen still sends one frame this often, so a decoder that joined
    /// late or dropped a keyframe recovers without the user touching anything.
    public static let keepaliveInterval: TimeInterval = 5

    private var lastAdmitted: Date?
    private var droppedCount = 0

    public init() {}

    public var dropped: Int { droppedCount }

    /// True when the frame should be encoded and sent.
    public mutating func admit(status: ScreenShareFrameStatus, dirtyRects: Int, now: Date) -> Bool {
        guard status == .complete else { droppedCount += 1; return false }
        if dirtyRects > 0 {
            lastAdmitted = now
            return true
        }
        guard let lastAdmitted else {
            // The very first frame is always sent: the phone has nothing to show
            // until it arrives, however still the screen is.
            self.lastAdmitted = now
            return true
        }
        guard now.timeIntervalSince(lastAdmitted) >= Self.keepaliveInterval else {
            droppedCount += 1
            return false
        }
        self.lastAdmitted = now
        return true
    }

    public mutating func reset() { lastAdmitted = nil; droppedCount = 0 }
}

// MARK: - Capture plan

/// One capture layer. Zoomed in, the Mac sends two: the region at the full
/// pixel budget, and a cheap whole-screen overview underneath it so the phone
/// never shows a black border around what it is reading.
public struct ScreenShareCaptureLayer: Sendable, Equatable {
    public enum Kind: String, Sendable, Equatable {
        /// What the user is reading: the zoom region, or the whole display.
        case primary
        /// The whole display at low resolution and a low frame rate.
        case overview
    }

    public var kind: Kind
    /// The part of the display this layer captures, in display points.
    public var sourceRect: CGRect
    public var width: Int
    public var height: Int
    public var fps: Int

    public init(kind: Kind, sourceRect: CGRect, width: Int, height: Int, fps: Int) {
        self.kind = kind; self.sourceRect = sourceRect
        self.width = width; self.height = height; self.fps = fps
    }
}

public struct ScreenShareCapturePlan: Sendable, Equatable {
    public var displayId: UInt32
    public var layers: [ScreenShareCaptureLayer]
    public var region: ScreenShareZoomRegion

    public init(displayId: UInt32, layers: [ScreenShareCaptureLayer], region: ScreenShareZoomRegion) {
        self.displayId = displayId; self.layers = layers; self.region = region
    }

    public var primary: ScreenShareCaptureLayer? { layers.first { $0.kind == .primary } }
    public var overview: ScreenShareCaptureLayer? { layers.first { $0.kind == .overview } }
}

/// Turns a display, a zoom region and a quality ceiling into the layers the
/// capture backend configures.
///
/// Terminal legibility comes first: zoomed in, the whole pixel budget goes to
/// the region the user pinched, so text is sent at (or above) its native size
/// instead of being downscaled with the rest of the screen.
public enum ScreenShareCapturePlanner {
    /// The overview layer never costs more than this. Matches the phone's own
    /// `OVERVIEW_MAX_WIDTH`, so neither side rescales what the other sent.
    public static let overviewMaxWidth = 640
    public static let overviewFps = 2

    public static func plan(
        displayId: UInt32, displayBounds: CGRect,
        region: ScreenShareZoomRegion, quality: ScreenShareQualityProfile
    ) -> ScreenShareCapturePlan {
        let region = region.normalized ?? .full
        let rect = CGRect(
            x: displayBounds.origin.x + region.x * displayBounds.width,
            y: displayBounds.origin.y + region.y * displayBounds.height,
            width: max(1, region.width * displayBounds.width),
            height: max(1, region.height * displayBounds.height))
        let size = ScreenShareQuality.fit(
            width: Int(rect.width.rounded()), height: Int(rect.height.rounded()),
            maxWidth: quality.width, maxHeight: quality.height)
        let primary = ScreenShareCaptureLayer(
            kind: .primary, sourceRect: rect, width: size.width, height: size.height, fps: quality.fps)
        guard !region.isFullScreen else {
            return ScreenShareCapturePlan(displayId: displayId, layers: [primary], region: region)
        }
        let overviewSize = ScreenShareQuality.fit(
            width: Int(displayBounds.width.rounded()), height: Int(displayBounds.height.rounded()),
            maxWidth: overviewMaxWidth,
            maxHeight: max(2, Int((Double(overviewMaxWidth) * displayBounds.height / max(1, displayBounds.width)).rounded())))
        let overview = ScreenShareCaptureLayer(
            kind: .overview, sourceRect: displayBounds,
            width: overviewSize.width, height: overviewSize.height, fps: overviewFps)
        return ScreenShareCapturePlan(displayId: displayId, layers: [primary, overview], region: region)
    }
}

// MARK: - Backend

/// One captured frame on its way to the encoder.
public struct ScreenShareVideoFrame: @unchecked Sendable {
    public var pixelBuffer: CVPixelBuffer
    public var layer: ScreenShareCaptureLayer.Kind
    /// Presentation time in nanoseconds on the host clock.
    public var timestampNanos: Int64

    public init(pixelBuffer: CVPixelBuffer, layer: ScreenShareCaptureLayer.Kind, timestampNanos: Int64) {
        self.pixelBuffer = pixelBuffer; self.layer = layer; self.timestampNanos = timestampNanos
    }
}

/// Where frames actually come from. `SCStreamCaptureBackend` in production; a
/// test drives its own, so the whole capture lifecycle runs headless.
public protocol ScreenShareCaptureBackend: Sendable {
    /// Starts (or reconfigures) capture. Throws when the Mac has no Screen
    /// Recording grant, which the routes turn into `screen-permission`.
    func apply(_ plan: ScreenShareCapturePlan) async throws
    func stop() async
}

/// Why capture could not start.
public enum ScreenShareCaptureError: Error, Sendable, Equatable {
    /// Screen Recording is not granted, or the grant expired after an update.
    case permissionDenied
    case displayGone
    case backendFailure
}

/// Owns the capture side of a session: the plan in force, the display being
/// captured, the zoom region, and the idle-frame gate.
///
/// Safety does not live here — `ScreenShareHost` decides whether capture may run
/// at all. This actor only does what it was told, and tells the caller when the
/// display it was watching disappeared.
public actor ScreenShareCaptureController {
    private let backend: ScreenShareCaptureBackend
    private let displays: ScreenShareDisplaySource
    private var plan: ScreenShareCapturePlan?
    private var quality: ScreenShareQualityProfile
    private var region: ScreenShareZoomRegion = .full
    /// One gate per layer: the 2 fps overview must not reset the primary
    /// layer's keepalive clock, nor the other way round.
    private var gates: [ScreenShareCaptureLayer.Kind: ScreenShareFrameGate] = [:]
    private var lastDisplayId: UInt32?
    private let now: @Sendable () -> Date

    public init(
        backend: ScreenShareCaptureBackend,
        displays: ScreenShareDisplaySource,
        quality: ScreenShareQualityProfile = ScreenShareQuality.wifiCeiling,
        now: @escaping @Sendable () -> Date = { Date() }
    ) {
        self.backend = backend; self.displays = displays; self.quality = quality; self.now = now
    }

    public var currentPlan: ScreenShareCapturePlan? { plan }
    public var zoomRegion: ScreenShareZoomRegion { region }
    public var droppedIdleFrames: Int { gates.values.reduce(0) { $0 + $1.dropped } }
    /// The display capture runs on (or last ran on, when it is stopped).
    public var displayId: UInt32? { plan?.displayId ?? lastDisplayId }

    /// Starts capture of `displayId`, falling back to the main display when that
    /// id is not attached. Returns the display actually captured.
    @discardableResult
    public func start(displayId: UInt32, quality: ScreenShareQualityProfile) async throws -> UInt32 {
        self.quality = quality
        gates = [:]
        return try await configure(displayId: displayId)
    }

    /// Mid-session display switch (the phone's switcher).
    @discardableResult
    public func setDisplay(_ displayId: UInt32) async throws -> UInt32 {
        // A new display invalidates the old zoom region: the pinch was over
        // other pixels, so the switch starts from the whole screen.
        region = .full
        gates = [:]
        return try await configure(displayId: displayId)
    }

    /// The phone pinched. A full-screen region drops the overview layer again.
    public func setZoom(_ requested: ScreenShareZoomRegion) async throws {
        guard let plan else { return }
        region = requested.normalized ?? .full
        gates = [:]
        _ = try await configure(displayId: plan.displayId)
    }

    public func setQuality(_ quality: ScreenShareQualityProfile) async throws {
        guard let plan else { self.quality = quality; return }
        self.quality = quality
        _ = try await configure(displayId: plan.displayId)
    }

    public func stop() async {
        plan = nil
        gates = [:]
        await backend.stop()
    }

    /// True when this frame carries new pixels and should be encoded. A frame
    /// that arrives while capture is stopped (a late one, in flight when the
    /// lock screen or a kill stopped the stream) is never admitted.
    public func admit(layer: ScreenShareCaptureLayer.Kind, status: ScreenShareFrameStatus, dirtyRects: Int) -> Bool {
        guard plan?.layers.contains(where: { $0.kind == layer }) == true else { return false }
        return gates[layer, default: ScreenShareFrameGate()].admit(status: status, dirtyRects: dirtyRects, now: now())
    }

    private func configure(displayId: UInt32) async throws -> UInt32 {
        var target = displayId
        var bounds = displays.bounds(of: target)
        if bounds == nil {
            // Hot-unplug: fall back to the main display rather than stopping.
            target = displays.mainDisplayId()
            bounds = displays.bounds(of: target)
            region = .full
        }
        guard let bounds else { throw ScreenShareCaptureError.displayGone }
        let fresh = ScreenShareCapturePlanner.plan(
            displayId: target, displayBounds: bounds, region: region, quality: quality)
        // The plan is in force before the backend starts, so the very first
        // frame — which a still screen may never follow with another — is not
        // refused as arriving from a stream nobody asked for.
        let previous = plan
        plan = fresh
        do { try await backend.apply(fresh) } catch { plan = previous; throw error }
        lastDisplayId = target
        return target
    }
}
