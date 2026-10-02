import AVFoundation
import CoreImage
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
final class SCStreamCaptureBackend: NSObject, ScreenShareCaptureBackend, @unchecked Sendable {
    /// Where an admitted frame goes. Set once, when the engine exists.
    typealias FrameHandler = @Sendable (ScreenShareVideoFrame, ScreenShareFrameStatus, Int) async -> Void

    private let lock = NSLock()
    private var streams: [ScreenShareCaptureLayer.Kind: SCStream] = [:]
    private var outputs: [ScreenShareCaptureLayer.Kind: LayerOutput] = [:]
    private var handler: FrameHandler?
    private let queue = DispatchQueue(label: "dev.mightyclaude.screen-share.capture", qos: .userInitiated)

    func setFrameHandler(_ handler: @escaping FrameHandler) {
        lock.lock(); self.handler = handler; lock.unlock()
    }

    func apply(_ plan: ScreenShareCapturePlan) async throws {
        let display = try await display(for: plan.displayId)
        // Our own windows are excluded: a remote viewer must not be shown the
        // window that is showing them.
        let filter = SCContentFilter(display: display, excludingApplications: [], exceptingWindows: [])
        var wanted = Set<ScreenShareCaptureLayer.Kind>()
        for layer in plan.layers {
            wanted.insert(layer.kind)
            try await start(layer: layer, filter: filter, displayBounds: display.frame)
        }
        for kind in streams.keys where !wanted.contains(kind) { await stop(kind: kind) }
    }

    func stop() async {
        for kind in currentKinds() { await stop(kind: kind) }
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
            // `screen-permission` so the phone says "Mac에서 승인 필요".
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
        configuration.pixelFormat = kCVPixelFormatType_420YpCbCr8BiPlanarFullRange
        configuration.colorSpaceName = CGColorSpace.sRGB
        configuration.showsCursor = true
        // A shallow queue is what makes a stalled consumer drop frames instead of
        // building a backlog the viewer would see as lag.
        configuration.queueDepth = 3
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

        let output = LayerOutput(kind: layer.kind) { [weak self] frame, status, dirty in
            guard let handler = self?.frameHandler() else { return }
            await handler(frame, status, dirty)
        }
        let stream = SCStream(filter: filter, configuration: configuration, delegate: nil)
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

    private func frameHandler() -> FrameHandler? {
        lock.lock(); defer { lock.unlock() }
        return handler
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

/// Reads the frame status and the dirty rectangles off one sample buffer, so the
/// idle-frame gate upstream can drop what carries no new pixels.
private final class LayerOutput: NSObject, SCStreamOutput {
    private let kind: ScreenShareCaptureLayer.Kind
    private let deliver: @Sendable (ScreenShareVideoFrame, ScreenShareFrameStatus, Int) async -> Void

    init(kind: ScreenShareCaptureLayer.Kind,
         deliver: @escaping @Sendable (ScreenShareVideoFrame, ScreenShareFrameStatus, Int) async -> Void) {
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
        let dirty = (info[.dirtyRects] as? [[String: Any]])?.count ?? 0
        let timestamp = CMSampleBufferGetPresentationTimeStamp(sampleBuffer)
        let nanos = CMTimeGetSeconds(timestamp).isFinite ? Int64(CMTimeGetSeconds(timestamp) * 1_000_000_000) : 0
        let frame = ScreenShareVideoFrame(pixelBuffer: pixelBuffer, layer: kind, timestampNanos: nanos)
        let deliver = deliver
        Task { await deliver(frame, status, dirty) }
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

/// Turns an overview frame into a small JPEG. The overview is a backdrop under
/// the zoomed region, so it goes down the data channel as an occasional still
/// rather than taking a second video track.
struct CoreImageScreenShareStillEncoder: ScreenShareStillEncoder {
    private let context = CIContext(options: [.useSoftwareRenderer: false])

    func jpeg(_ frame: ScreenShareVideoFrame, quality: Double) -> Data? {
        let image = CIImage(cvPixelBuffer: frame.pixelBuffer)
        guard let colorSpace = CGColorSpace(name: CGColorSpace.sRGB) else { return nil }
        return context.jpegRepresentation(
            of: image, colorSpace: colorSpace,
            options: [kCGImageDestinationLossyCompressionQuality as CIImageRepresentationOption:
                        max(0.1, min(1, quality))])
    }
}
