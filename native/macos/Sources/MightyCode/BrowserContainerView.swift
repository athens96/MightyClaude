import SwiftUI
import AppKit

// NSViewRepresentable wrapper that embeds the CEF browser NSView in the SwiftUI hierarchy.
struct BrowserContainerView: NSViewRepresentable {
    @ObservedObject var engine: CefBrowserEngine

    func makeNSView(context: Context) -> NSView {
        engine.containerView
    }

    func updateNSView(_ nsView: NSView, context: Context) {
        if let superview = nsView.superview {
            nsView.frame = superview.bounds
        }
    }
}
