import AppKit
import Darwin
import MightyCore

// BrowserEngine implementation for a browser pane.
//
// The pane keeps the rules on the Swift side: this type owns the workspace
// profile directory and its stale-lock recovery, the back/forward history and
// the BrowserNavigationState the navigation bar reads, and the NSView the CEF
// browser is parented to. The CEF browser itself is created and driven by
// MightyCEFBridge.dylib, which a MIGHTY_BROWSER_ENGINE=1 build ships next to
// the Chromium Embedded Framework. The bridge is opened with dlopen at
// runtime, so the Swift package still builds and tests with no engine
// downloaded; without it the pane shows browser.engine.missing.
final class CefBrowserEngine: NSObject, BrowserEngine, ObservableObject, @unchecked Sendable {
    @Published private(set) var navState = BrowserNavigationState()

    let profilePath: URL

    private let _containerView: NSView
    private var history = BrowserHistory()
    private var bridge: UnsafeMutableRawPointer?
    private var engineReady = false

    init(profileKey: String) {
        _containerView = BrowserHostView(frame: .zero)
        // Every workspace keeps its own persistent CEF profile; a lock left
        // behind by a crash is cleared before anything opens the directory.
        profilePath = BrowserProfileSupport.profilePath(workspaceProfileKey: profileKey)
        try? FileManager.default.createDirectory(at: profilePath, withIntermediateDirectories: true)
        BrowserProfileSupport.clearStaleLock(at: profilePath)
        super.init()
        openBridge()
    }

    deinit {
        // Closing the tab closes that browser.
        if let bridge, let closeSym = dlsym(bridge, "mighty_cef_close") {
            typealias CloseFn = @convention(c) (UnsafeMutableRawPointer) -> Void
            unsafeBitCast(closeSym, to: CloseFn.self)(Unmanaged.passUnretained(_containerView).toOpaque())
        }
        if let bridge { dlclose(bridge) }
    }

    var containerView: NSView { _containerView }

    // MARK: - BrowserEngine

    var isAvailable: Bool { engineReady }
    var canGoBack: Bool { navState.canGoBack }
    var canGoForward: Bool { navState.canGoForward }

    func loadURL(_ url: URL) {
        history.visit(url)
        present(history.current)
    }

    func goBack() {
        guard history.canGoBack else { return }
        present(history.goBack())
    }

    func goForward() {
        guard history.canGoForward else { return }
        present(history.goForward())
    }

    func reload() {
        present(history.current)
    }

    // MARK: - Internals

    // Single actuation point: the history decides which page the pane shows,
    // the state the navigation bar reads is refreshed from it, and the bridge
    // puts that page on screen inside `containerView`.
    private func present(_ url: URL?) {
        guard let url else { return }
        navState = history.state(isLoading: true)
        if !show(url) { navState = history.state(isLoading: false) }
    }

    private func openBridge() {
        guard case .available(let frameworkPath) = BrowserEngineLocator.locate(),
              let frameworks = Bundle.main.privateFrameworksPath,
              let handle = dlopen("\(frameworks)/MightyCEFBridge.dylib", RTLD_NOW | RTLD_LOCAL) else { return }
        bridge = handle
        // The bridge opens the pinned framework and resolves the CEF entry
        // points; both have to answer before the pane claims an engine.
        typealias LoadFn = @convention(c) (UnsafePointer<CChar>) -> Int32
        typealias SymFn = @convention(c) () -> UnsafeMutableRawPointer?
        guard let loadSym = dlsym(handle, "mighty_cef_load"),
              let initSym = dlsym(handle, "mighty_cef_initialize_sym"),
              let createSym = dlsym(handle, "mighty_cef_create_browser_sym") else { return }
        let binary = frameworkPath.appendingPathComponent("Chromium Embedded Framework").path
        guard binary.withCString({ unsafeBitCast(loadSym, to: LoadFn.self)($0) }) != 0 else { return }
        engineReady = unsafeBitCast(initSym, to: SymFn.self)() != nil
            && unsafeBitCast(createSym, to: SymFn.self)() != nil
        if engineReady { CefBrowserEngine.startMessagePump(handle) }
    }

    @discardableResult
    private func show(_ url: URL) -> Bool {
        guard engineReady, let bridge, let showSym = dlsym(bridge, "mighty_cef_show") else { return false }
        typealias ShowFn = @convention(c) (UnsafeMutableRawPointer, Int32, Int32,
                                           UnsafePointer<CChar>, UnsafePointer<CChar>) -> Int32
        let show = unsafeBitCast(showSym, to: ShowFn.self)
        let parent = Unmanaged.passUnretained(_containerView).toOpaque()
        let bounds = _containerView.bounds
        return profilePath.path.withCString { cache in
            url.absoluteString.withCString { address in
                show(parent, Int32(bounds.width), Int32(bounds.height), cache, address) != 0
            }
        }
    }

    // MARK: - Engine lifecycle

    private static var pumpTimer: Timer?

    // CEF's message loop is pumped from the app's main run loop so SwiftUI is
    // never blocked, and the engine is shut down cleanly when the app quits.
    private static func startMessagePump(_ handle: UnsafeMutableRawPointer) {
        guard pumpTimer == nil, let workSym = dlsym(handle, "mighty_cef_work") else { return }
        typealias VoidFn = @convention(c) () -> Void
        let work = unsafeBitCast(workSym, to: VoidFn.self)
        let timer = Timer(timeInterval: 1.0 / 60.0, repeats: true) { _ in work() }
        RunLoop.main.add(timer, forMode: .common)
        pumpTimer = timer
        NotificationCenter.default.addObserver(forName: NSApplication.willTerminateNotification,
                                               object: nil, queue: .main) { _ in
            pumpTimer?.invalidate()
            pumpTimer = nil
            if let shutdownSym = dlsym(handle, "mighty_cef_shutdown") {
                unsafeBitCast(shutdownSym, to: VoidFn.self)()
            }
        }
    }
}

// Resizing the pane resizes the browser: CEF parents its own NSView here, so
// the host keeps every child filling its bounds.
final class BrowserHostView: NSView {
    override var isFlipped: Bool { true }

    override func resizeSubviews(withOldSize oldSize: NSSize) {
        for subview in subviews { subview.frame = bounds }
    }

    override func layout() {
        super.layout()
        for subview in subviews { subview.frame = bounds }
    }
}
