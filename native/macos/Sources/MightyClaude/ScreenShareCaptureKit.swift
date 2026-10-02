import CoreMedia
import Foundation
import MightyCore
import ScreenCaptureKit

/// ScreenCaptureKit capture for screen sharing.
///
/// One `SCStream` per layer of the plan: the primary layer is what the user is
/// reading (the whole display, or the region they pinched into at the full pixel
/// budget), and the overview layer is the cheap whole-screen picture the zoomed
/// region sits on top of.
///
/// Zero hertz is doing most of the work here: ScreenCaptureKit stops delivering
/// while nothing changes, and `ScreenShareFrameGate` drops what still arrives
/// with no dirty rectangle. An untouched screen therefore costs almost nothing.
final class SCStreamCaptureBackend: NSObject, ScreenShareCaptureBackend, SCStreamDelegate, @unchecked Sendable {
    typealias FrameHandler = @Sendable (ScreenShareVideoFrame, ScreenShareFrameStatus, Int) async -> Void
    typealias InterruptionHandler = @Sendable () async -> Void

    private struct Delivery: @unchecked Sendable {
        var frame: ScreenShareVideoFrame
        var status: ScreenShareFrameStatus
        var dirtyRects: Int
    }

    private let lock = NSLock()
    private var streams: [ScreenShareCaptureLayer.Kind: SCStream] = [:]
    private var outputs: [ScreenShareCaptureLayer.Kind: LayerOutput] = [:]
    private var interrupted: InterruptionHandler?
    private let queue = DispatchQueue(label: "dev.mightyclaude.screen-share.capture", qos: .userInitiated)
    /// Frames leave in the order they were captured, through one consumer. The
    /// buffer keeps only the newest few: an encoder that falls behind skips
    /// stale frames instead of building a backlog the viewer would see as lag.
    private let deliveries: AsyncStream<Delivery>.Continuation
    private let consumer: Task<Void, Never>
    private let handlerBox = HandlerBox()

    override init() {
        let (stream, continuation) = AsyncStream<Delivery>.makeStream(bufferingPolicy: .bufferingNewest(4))
        deliveries = continuation
        let box = handlerBox
        consumer = Task.detached(priority: .userInitiated) {
            for await delivery in stream {
                await box.handler?(delivery.frame, delivery.status, delivery.dirtyRects)
            }
        }
        super.init()
    }

    deinit {
        deliveries.finish()
        consumer.cancel()
    }

    func setHandlers(frame: @escaping FrameHandler, interrupted: @escaping InterruptionHandler) {
        handlerBox.handler = frame
        lock.lock(); self.interrupted = interrupted; lock.unlock()
    }

    func apply(_ plan: ScreenShareCapturePlan) async throws {
        let display = try await display(for: plan.displayId)
        // Every window is shared, this app's included: the phone is usually
        // there to read the terminal panes this app shows.
        let filter = SCContentFilter(display: display, excludingApplications: [], exceptingWindows: [])
        var wanted = Set<ScreenShareCaptureLayer.Kind>()
        for layer in plan.layers {
            wanted.insert(layer.kind)
            try await start(layer: layer, filter: filter, displayBounds: display.frame)
        }
        for kind in currentKinds() where !wanted.contains(kind) { await stop(kind: kind) }
    }

    func stop() async {
        for kind in currentKinds() { await stop(kind: kind) }
    }

    // MARK: SCStreamDelegate

    /// The stream ended without being asked to: its display went away, or the
    /// system revoked it. The engine decides what comes next.
    func stream(_ stream: SCStream, didStopWithError error: Error) {
        lock.lock()
        let known = streams.first { $0.value === stream }?.key
        if let known { streams.removeValue(forKey: known); outputs.removeValue(forKey: known) }
        let handler = interrupted
        lock.unlock()
        guard known != nil, let handler else { return }
        Task { await handler() }
    }

    // MARK: Internals

    private func currentKinds() -> [ScreenShareCaptureLayer.Kind] {
        lock.lock(); defer { lock.unlock() }
        return Array(streams.keys)
    }

    private func display(for displayId: UInt32) async throws -> SCDisplay {
        let content: SCShareableContent
        do { content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true) }
        catch {
            // The only reason this fails in practice is a missing or expired
            // Screen Recording grant, which the routes turn into
            // `screen-permission` so the phone asks for approval on the Mac.
            throw ScreenShareCaptureError.permissionDenied
        }
        guard let display = content.displays.first(where: { $0.displayID == displayId })
                ?? content.displays.first
        else { throw ScreenShareCaptureError.displayGone }
        return display
    }

    private func start(layer: ScreenShareCaptureLayer, filter: SCContentFilter, displayBounds: CGRect) async throws {
        let configuration = SCStreamConfiguration()
        configuration.width = layer.width
        configuration.height = layer.height
        configuration.minimumFrameInterval = CMTime(value: 1, timescale: CMTimeScale(max(1, layer.fps)))
        // NV12, which the VideoToolbox H.264 encoder takes without a conversion.
        configuration.pixelFormat = kCVPixelFormatType_420YpCbCr8BiPlanarFullRange
        configuration.colorSpaceName = CGColorSpace.sRGB
        configuration.showsCursor = true
        // A shallow queue makes a stalled consumer drop frames instead of
        // building a backlog; the encoder holds a few buffers of its own.
        configuration.queueDepth = 5
        configuration.scalesToFit = false
        // `sourceRect` is relative to the display's own origin, not the global
        // desktop, so a second display's rect starts at zero again.
        configuration.sourceRect = CGRect(
            x: layer.sourceRect.origin.x - displayBounds.origin.x,
            y: layer.sourceRect.origin.y - displayBounds.origin.y,
            width: layer.sourceRect.width, height: layer.sourceRect.height)

        if let existing = existingStream(layer.kind) {
            do {
                try await existing.updateConfiguration(configuration)
                try await existing.updateContentFilter(filter)
                return
            } catch {
                // A stream that refuses a reconfiguration is replaced rather than
                // left showing the previous region.
                await stop(kind: layer.kind)
            }
        }

        let continuation = deliveries
        let output = LayerOutput(kind: layer.kind) { frame, status, dirty in
            continuation.yield(Delivery(frame: frame, status: status, dirtyRects: dirty))
        }
        let stream = SCStream(filter: filter, configuration: configuration, delegate: self)
        do {
            try stream.addStreamOutput(output, type: .screen, sampleHandlerQueue: queue)
            try await stream.startCapture()
        } catch {
            throw ScreenShareCaptureError.permissionDenied
        }
        lock.lock(); streams[layer.kind] = stream; outputs[layer.kind] = output; lock.unlock()
    }

    private func existingStream(_ kind: ScreenShareCaptureLayer.Kind) -> SCStream? {
        lock.lock(); defer { lock.unlock() }
        return streams[kind]
    }

    private func stop(kind: ScreenShareCaptureLayer.Kind) async {
        lock.lock()
        let stream = streams.removeValue(forKey: kind)
        let output = outputs.removeValue(forKey: kind)
        lock.unlock()
        guard let stream else { return }
        if let output { try? stream.removeStreamOutput(output, type: .screen) }
        try? await stream.stopCapture()
    }
}

/// The frame handler, settable after the consumer task already runs.
private final class HandlerBox: @unchecked Sendable {
    private let lock = NSLock()
    private var value: SCStreamCaptureBackend.FrameHandler?
    var handler: SCStreamCaptureBackend.FrameHandler? {
        get { lock.lock(); defer { lock.unlock() }; return value }
        set { lock.lock(); value = newValue; lock.unlock() }
    }
}

/// Reads the frame status and the dirty rectangles off one sample buffer, so the
/// idle-frame gate upstream can drop what carries no new pixels.
private final class LayerOutput: NSObject, SCStreamOutput {
    private let kind: ScreenShareCaptureLayer.Kind
    private let deliver: @Sendable (ScreenShareVideoFrame, ScreenShareFrameStatus, Int) -> Void

    init(kind: ScreenShareCaptureLayer.Kind,
         deliver: @escaping @Sendable (ScreenShareVideoFrame, ScreenShareFrameStatus, Int) -> Void) {
        self.kind = kind; self.deliver = deliver
    }

    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of type: SCStreamOutputType) {
        guard type == .screen, CMSampleBufferIsValid(sampleBuffer),
              let pixelBuffer = CMSampleBufferGetImageBuffer(sampleBuffer)
        else { return }
        let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: false)
            as? [[SCStreamFrameInfo: Any]]
        let info = attachments?.first ?? [:]
        let status = Self.status(info[.status])
        let dirty = (info[.dirtyRects] as? [Any])?.count ?? 0
        let seconds = CMTimeGetSeconds(CMSampleBufferGetPresentationTimeStamp(sampleBuffer))
        let nanos = seconds.isFinite ? Int64(seconds * 1_000_000_000) : Int64(DispatchTime.now().uptimeNanoseconds)
        deliver(ScreenShareVideoFrame(pixelBuffer: pixelBuffer, layer: kind, timestampNanos: nanos), status, dirty)
    }

    private static func status(_ raw: Any?) -> ScreenShareFrameStatus {
        guard let value = raw as? Int, let status = SCFrameStatus(rawValue: value) else { return .complete }
        switch status {
        case .complete: return .complete
        case .idle: return .idle
        case .blank: return .blank
        case .suspended: return .suspended
        case .started: return .started
        case .stopped: return .stopped
        @unknown default: return .complete
        }
    }
}
