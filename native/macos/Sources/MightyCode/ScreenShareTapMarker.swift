import AppKit
import MightyCore

/// The latency marker: a ring drawn for half a second where a phone tapped,
/// so every timed tap changes pixels the stream has to carry.
///
/// It lives in a borderless, click-through panel above everything on that
/// display. ScreenCaptureKit captures this app's windows too (see
/// `SCStreamCaptureBackend.apply`), so the ring reaches the phone in the same
/// stream as the tap's own effect, and it never takes a click or focus away
/// from what the tap landed on.
final class ScreenShareTapMarkerOverlay: ScreenShareTapMarkerSurface, @unchecked Sendable {
    /// Ring diameter in points: large enough to show at 720p on a phone.
    static let side: CGFloat = 48

    @MainActor private var panel: NSPanel?
    @MainActor private var generation = 0

    func showMarker(at position: CGPoint, displayId: UInt32) async -> Bool {
        await present(at: position)
    }

    @MainActor private func present(at position: CGPoint) -> Bool {
        // Global display coordinates start at the top-left of the primary
        // display with y growing down; AppKit's start at its bottom-left with y
        // growing up. Both share the primary display's height.
        guard let primary = NSScreen.screens.first else { return false }
        let center = NSPoint(x: position.x, y: primary.frame.maxY - position.y)
        let rect = NSRect(x: center.x - Self.side / 2, y: center.y - Self.side / 2, width: Self.side, height: Self.side)
        let panel = self.panel ?? Self.makePanel()
        self.panel = panel
        panel.setFrame(rect, display: true)
        panel.orderFrontRegardless()
        panel.display()
        generation += 1
        let shown = generation
        Task { @MainActor [weak self] in
            try? await Task.sleep(nanoseconds: UInt64(ScreenShareTapMarker.visibleSeconds * 1_000_000_000))
            guard let self, self.generation == shown else { return }
            self.panel?.orderOut(nil)
        }
        return true
    }

    @MainActor private static func makePanel() -> NSPanel {
        let panel = NSPanel(contentRect: NSRect(x: 0, y: 0, width: side, height: side),
                            styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = false
        panel.ignoresMouseEvents = true
        panel.hidesOnDeactivate = false
        panel.isReleasedWhenClosed = false
        panel.animationBehavior = .none
        panel.level = .screenSaver
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
        // Captured like any other window; `.none` would hide it from the stream.
        panel.sharingType = .readOnly
        panel.contentView = TapMarkerView(frame: NSRect(x: 0, y: 0, width: side, height: side))
        return panel
    }
}

/// Black, white and magenta rings: visible on a light page, a dark terminal
/// and anything in between.
private final class TapMarkerView: NSView {
    override func draw(_ dirtyRect: NSRect) {
        let bounds = self.bounds.insetBy(dx: 3, dy: 3)
        NSColor.black.setStroke()
        let outer = NSBezierPath(ovalIn: bounds)
        outer.lineWidth = 6
        outer.stroke()
        NSColor.white.setStroke()
        let middle = NSBezierPath(ovalIn: bounds.insetBy(dx: 5, dy: 5))
        middle.lineWidth = 4
        middle.stroke()
        NSColor.magenta.setFill()
        NSBezierPath(ovalIn: bounds.insetBy(dx: bounds.width * 0.32, dy: bounds.height * 0.32)).fill()
    }
}
