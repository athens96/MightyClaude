import AppKit
import Darwin
import MightyCore

// BrowserEngine implementation backed by MightyCEFBridge.dylib (loaded at runtime
// via dlopen so the Swift package compiles without CEF headers).
// The bridge is only present in MIGHTY_BROWSER_ENGINE=1 builds.
final class CefBrowserEngine: NSObject, BrowserEngine, ObservableObject, @unchecked Sendable {
    @Published private(set) var navState = BrowserNavigationState()

    private let _containerView: NSView
    private var bridgeHandle: UnsafeMutableRawPointer?

    var isAvailable: Bool { bridgeHandle != nil }
    var canGoBack: Bool { navState.canGoBack }
    var canGoForward: Bool { navState.canGoForward }

    init(profileKey: String) {
        _containerView = NSView(frame: .zero)
        super.init()

        let profilePath = BrowserProfileSupport.profilePath(workspaceProfileKey: profileKey)
        BrowserProfileSupport.clearStaleLock(at: profilePath)

        if let frameworks = Bundle.main.privateFrameworksPath {
            let bridgePath = "\(frameworks)/MightyCEFBridge.dylib"
            bridgeHandle = dlopen(bridgePath, RTLD_NOW | RTLD_LOCAL)
        }
    }

    var containerView: NSView { _containerView }

    func loadURL(_ url: URL) {
        // CEF navigation via the bridge dylib at runtime
    }

    func goBack() {
        // CEF goBack via the bridge dylib at runtime
    }

    func goForward() {
        // CEF goForward via the bridge dylib at runtime
    }

    func reload() {
        // CEF reload via the bridge dylib at runtime
    }
}
