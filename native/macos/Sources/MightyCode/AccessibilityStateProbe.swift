import AppKit
import SwiftUI

/// An AppKit twin of a SwiftUI control's identifier and enabled state.
///
/// SwiftUI builds a control's accessibility node only while an assistive client
/// is reading the app. On a machine with none, such as a CI runner, the
/// composer's buttons are missing from the tree and the GUI smoke cannot tell
/// which action is shown. This empty view sits behind the control, exists only
/// while the control does, and carries the same identifier and enabled state.
/// It is not an accessibility element, so VoiceOver still reads the control itself.
struct AccessibilityStateProbe: NSViewRepresentable {
    let identifier: String
    let enabled: Bool
    func makeNSView(context: Context) -> AccessibilityStateProbeView { AccessibilityStateProbeView() }
    func updateNSView(_ view: AccessibilityStateProbeView, context: Context) {
        view.setAccessibilityElement(false)
        view.setAccessibilityIdentifier(identifier)
        view.enabled = enabled
    }
}

final class AccessibilityStateProbeView: NSView {
    var enabled = true
    override func isAccessibilityEnabled() -> Bool { enabled }
    override func hitTest(_ point: NSPoint) -> NSView? { nil }
}
