import AppKit

// NSApplication subclass that satisfies CEF's CefAppProtocol requirement on macOS.
// NSPrincipalClass in Info.plist names this class so NSApplicationMain instantiates
// it as NSApp; CEF checks isHandlingSendEvent/setHandlingSendEvent via the ObjC runtime.
@objc(MightyApplication)
final class MightyApplication: NSApplication {
    private var _handlingSendEvent = false

    @objc var isHandlingSendEvent: Bool { _handlingSendEvent }

    @objc func setHandlingSendEvent(_ handlingSendEvent: Bool) {
        _handlingSendEvent = handlingSendEvent
    }

    override func sendEvent(_ event: NSEvent) {
        if _handlingSendEvent {
            super.sendEvent(event)
            return
        }
        _handlingSendEvent = true
        super.sendEvent(event)
        _handlingSendEvent = false
    }
}
