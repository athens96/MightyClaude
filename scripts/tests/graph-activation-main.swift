import AppKit
import SwiftUI

@MainActor final class ApplicationFixture {
    var isActive = true
    var keyWindow: NSWindow?
}
@MainActor let NSApp = ApplicationFixture()
@MainActor final class WindowFixture: NSWindow {
    var simulatedKey = true
    var dispatchCount = 0
    override var isKeyWindow: Bool { simulatedKey }
    override func sendEvent(_ event: NSEvent) { dispatchCount += 1 }
}
// Camera geometry is unrelated to event ownership. The actual camera source
// is compiled below, with the independent node-id dependency provided here.
enum MightyGraphBlockSize {
    static func nodeID(runID: String, suffix: String) -> String { runID + ":" + suffix }
}
enum MightyBubbleShape { static let tailLength: CGFloat = 12 }
enum MightyGraphReferenceBubble {
    static let minimumWidth: CGFloat = 300
    static let minimumHeight: CGFloat = 200
    static let margin: CGFloat = 12
}
struct MightyOverlayLayout {
    var onLeft = false
    func frame(in size: CGSize) -> CGRect { CGRect(x: 450, y: 12, width: 330, height: 220) }
}
enum MightyGraphLayout {
    static func cameraOffset(for frame: CGRect, viewport: CGSize, zoom: CGFloat, alignTop: Bool) -> CGPoint { .zero }
}
// PRODUCTION_CAMERA
// PRODUCTION_PROBE

extension MightyGraphInteractionProbe {
    func fixtureRoute(_ event: NSEvent) -> NSEvent? { handle(event) }
}

@main struct Main {
    @MainActor static func main() {
        _ = NSApplication.shared
        let realActive = NSApplication.shared.isActive
        let realKey = NSApplication.shared.keyWindow
        let window = WindowFixture(contentRect: NSRect(x: -10000, y: -10000, width: 900, height: 600), styleMask: [.titled], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        NSApp.keyWindow = window
        let content = NSView(frame: NSRect(x: 0, y: 0, width: 900, height: 600))
        let root = NSView(frame: content.bounds)
        let probe = MightyGraphInteractionProbe(frame: content.bounds)
        probe.graphRoot = root
        probe.expectedViewportSize = content.bounds.size
        content.addSubview(root); content.addSubview(probe)
        window.contentView = content
        let text = NSTextView(frame: NSRect(x: 600, y: 400, width: 200, height: 40))
        root.addSubview(text)
        var results: [String: Bool] = [:]
        func event(_ type: NSEvent.EventType, _ point: CGPoint = CGPoint(x: 100, y: 100), key: UInt16 = 0) -> NSEvent {
            if type == .keyDown {
                return NSEvent.keyEvent(with: type, location: .zero, modifierFlags: [], timestamp: ProcessInfo.processInfo.systemUptime, windowNumber: window.windowNumber, context: nil, characters: key == 53 ? "\u{1b}" : "a", charactersIgnoringModifiers: key == 53 ? "\u{1b}" : "a", isARepeat: false, keyCode: key)!
            }
            return NSEvent.mouseEvent(with: type, location: probe.convert(point, to: nil), modifierFlags: [], timestamp: ProcessInfo.processInfo.systemUptime, windowNumber: window.windowNumber, context: nil, eventNumber: 1, clickCount: 1, pressure: type == .leftMouseUp ? 0 : 1)!
        }
        func send(_ value: NSEvent) -> Bool {
            let before = window.dispatchCount
            NSApplication.shared.sendEvent(value)
            return window.dispatchCount == before + 1
        }
        func reset() {
            NotificationCenter.default.post(name: NSWindow.didResignKeyNotification, object: window)
            NSApp.isActive = true; NSApp.keyWindow = window; window.simulatedKey = true
            probe.layoutFrames = []; probe.overlayLayout = nil; probe.selectedNodeID = nil
            _ = window.makeFirstResponder(text)
        }
        reset()
        NSApp.isActive = false; window.simulatedKey = false; NSApp.keyWindow = nil
        results["inactiveCanvasClickReachesWindow"] = send(event(.leftMouseDown))
        results["inactiveCanvasClickPreservesEditor"] = window.firstResponder === text
        results["inactiveCanvasClickDoesNotStartPan"] = !probe.isPanning
        reset(); window.simulatedKey = false; NSApp.keyWindow = nil
        results["nonKeyCanvasClickReachesWindow"] = send(event(.leftMouseDown))
        results["nonKeyCanvasClickPreservesEditor"] = window.firstResponder === text
        reset(); NSApp.keyWindow = nil
        results["inconsistentKeyStatePassesClick"] = send(event(.leftMouseDown))
        reset()
        results["activeCanvasStartsPan"] = !send(event(.leftMouseDown)) && probe.isPanning && window.firstResponder === probe
        results["activePanDoesNotConsumeTyping"] = probe.fixtureRoute(event(.keyDown)) != nil
        results["activePanConsumesOwnMouseUp"] = !send(event(.leftMouseUp)) && !probe.isPanning
        reset(); _ = send(event(.leftMouseDown)); NSApp.isActive = false
        results["deactivationCancelsPanWithoutNotification"] = send(event(.leftMouseDragged)) && !probe.isPanning
        reset(); probe.layoutFrames = [("node", CGRect(x: 50, y: 50, width: 350, height: 240))]
        let corner = CGPoint(x: 398, y: 288)
        NSApp.isActive = false; window.simulatedKey = false; NSApp.keyWindow = nil
        results["inactiveResizeClickReachesWindow"] = send(event(.leftMouseDown, corner))
        results["inactiveResizeDoesNotCapture"] = !probe.isResizing && window.firstResponder === text
        reset(); probe.layoutFrames = [("node", CGRect(x: 50, y: 50, width: 350, height: 240))]
        results["activeResizeStillStarts"] = !send(event(.leftMouseDown, corner)) && probe.isResizing
        window.simulatedKey = false; NSApp.keyWindow = nil
        results["lostKeyCancelsResizeWithoutNotification"] = send(event(.leftMouseDragged, CGPoint(x: 420, y: 315))) && !probe.isResizing
        reset(); probe.overlayLayout = MightyOverlayLayout()
        let overlayCorner = probe.overlayHandleRect!.center
        NSApp.isActive = false
        results["inactiveOverlayResizeClickReachesWindow"] = send(event(.leftMouseDown, overlayCorner))
        results["inactiveOverlayResizePreservesEditor"] = window.firstResponder === text
        reset(); probe.layoutFrames = [("text", probe.convert(text.bounds, from: text).insetBy(dx: -10, dy: -10))]
        results["activeTextClickReachesWindow"] = send(event(.leftMouseDown, probe.convert(NSPoint(x: 10, y: 10), from: text)))
        results["activeTextClickPreservesEditor"] = window.firstResponder === text
        probe.dispose()
        results["realApplicationActivationUnchanged"] = NSApplication.shared.isActive == realActive
        results["realApplicationKeyWindowUnchanged"] = NSApplication.shared.keyWindow === realKey
        window.contentView = nil; window.close()
        let data = try! JSONSerialization.data(withJSONObject: results, options: [.sortedKeys])
        print(String(decoding: data, as: UTF8.self))
    }
}
extension CGRect { var center: CGPoint { CGPoint(x: midX, y: midY) } }
