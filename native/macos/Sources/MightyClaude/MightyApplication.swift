import AppKit

// NSApplication subclass that satisfies CEF's CefAppProtocol requirement on macOS.
// MightyClaudeLauncher creates the singleton before SwiftUI's App.main(), which
// otherwise ignores NSPrincipalClass and creates SwiftUI.AppKitApplication.
// CEF checks isHandlingSendEvent/setHandlingSendEvent via the ObjC runtime.
@objc(MightyApplication)
final class MightyApplication: NSApplication {
    private var _handlingSendEvent = false

    @objc var isHandlingSendEvent: Bool { _handlingSendEvent }

    @objc func setHandlingSendEvent(_ handlingSendEvent: Bool) {
        _handlingSendEvent = handlingSendEvent
    }

    override func sendEvent(_ event: NSEvent) {
        let previous = _handlingSendEvent
        _handlingSendEvent = true
        defer { _handlingSendEvent = previous }
        super.sendEvent(event)
    }
}
