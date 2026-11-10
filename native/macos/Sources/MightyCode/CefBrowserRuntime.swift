import AppKit
import Darwin

/// CEF and its callbacks live for the process, independently of SwiftUI panes.
/// In particular, a pane must never dlclose the code called by the message pump.
final class CefBrowserRuntime {
    static let shared = CefBrowserRuntime()

    private typealias LoadFn = @convention(c) (UnsafePointer<CChar>) -> Int32
    private typealias ShowFn = @convention(c) (UnsafeMutableRawPointer, Int32, Int32,
                                             UnsafePointer<CChar>, UnsafePointer<CChar>) -> Int32
    private typealias ParentFn = @convention(c) (UnsafeMutableRawPointer) -> Void
    private typealias ClosedFn = @convention(c) (UnsafeMutableRawPointer) -> Int32
    private typealias StatusFn = @convention(c) () -> Int32
    private typealias VoidFn = @convention(c) () -> Void

    private var handle: UnsafeMutableRawPointer?
    private var initializeCEF: LoadFn?
    private var showBrowser: ShowFn?
    private var closeBrowser: ParentFn?
    private var isBrowserClosed: ClosedFn?
    private var hasBrowser: ClosedFn?
    private var isLoading: ClosedFn?
    private var isInitialized: StatusFn?
    private var work: VoidFn?
    private var shutdown: VoidFn?
    private var pumpTimer: Timer?
    private var terminationObserver: NSObjectProtocol?
    private var hostedViews: [ObjectIdentifier: NSView] = [:]
    private var closingViews: [ObjectIdentifier: NSView] = [:]
    private var observers: [ObjectIdentifier: () -> Void] = [:]
    private var loadAttempted = false
    private var initializationAttempted = false
    private var terminating = false
    private(set) var isAvailable = false

    private init() {}

    func load(frameworkBinary: URL, bridgePath: String) -> Bool {
        precondition(Thread.isMainThread)
        guard !terminating else { return false }
        guard !loadAttempted else { return isAvailable && !terminating }
        loadAttempted = true
        guard let library = dlopen(bridgePath, RTLD_NOW | RTLD_LOCAL) else { return false }
        // Deliberately keep this reference until process exit, even on a failed
        // initialization: CEF may already have registered Objective-C classes.
        handle = library
        guard let loadSymbol = dlsym(library, "mighty_cef_load"),
              let initializeSymbol = dlsym(library, "mighty_cef_initialize"),
              let showSymbol = dlsym(library, "mighty_cef_show"),
              let closeSymbol = dlsym(library, "mighty_cef_close"),
              let closedSymbol = dlsym(library, "mighty_cef_is_browser_closed"),
              let hasBrowserSymbol = dlsym(library, "mighty_cef_has_browser"),
              let loadingSymbol = dlsym(library, "mighty_cef_is_loading"),
              let initializedSymbol = dlsym(library, "mighty_cef_is_initialized"),
              let workSymbol = dlsym(library, "mighty_cef_work"),
              let shutdownSymbol = dlsym(library, "mighty_cef_shutdown") else { return false }
        let load = unsafeBitCast(loadSymbol, to: LoadFn.self)
        guard frameworkBinary.path.withCString({ load($0) }) != 0 else { return false }
        initializeCEF = unsafeBitCast(initializeSymbol, to: LoadFn.self)
        showBrowser = unsafeBitCast(showSymbol, to: ShowFn.self)
        closeBrowser = unsafeBitCast(closeSymbol, to: ParentFn.self)
        isBrowserClosed = unsafeBitCast(closedSymbol, to: ClosedFn.self)
        hasBrowser = unsafeBitCast(hasBrowserSymbol, to: ClosedFn.self)
        isLoading = unsafeBitCast(loadingSymbol, to: ClosedFn.self)
        isInitialized = unsafeBitCast(initializedSymbol, to: StatusFn.self)
        work = unsafeBitCast(workSymbol, to: VoidFn.self)
        shutdown = unsafeBitCast(shutdownSymbol, to: VoidFn.self)
        isAvailable = true
        return true
    }

    /// Called by the executable launcher before SwiftUI enters NSApplication's
    /// run loop. Chromium installs ENTRY/EXIT observers during initialization;
    /// initializing in a later SwiftUI task can deliver EXIT without ENTRY.
    func initialize(cacheRoot: URL) -> Bool {
        precondition(Thread.isMainThread)
        guard !terminating, isAvailable, let initializeCEF else { return false }
        guard !initializationAttempted else { return isInitializedNow }
        initializationAttempted = true
        guard cacheRoot.path.withCString({ initializeCEF($0) }) != 0, isInitializedNow else { return false }
        startMessagePump()
        return true
    }

    func show(in view: NSView, profile: URL, url: URL) -> Bool {
        precondition(Thread.isMainThread)
        guard isAvailable, isInitializedNow, !terminating, let showBrowser else { return false }
        let parent = Unmanaged.passUnretained(view).toOpaque()
        let accepted = profile.path.withCString { cache in
            url.absoluteString.withCString { address in
                showBrowser(parent, pixelDimension(view.bounds.width), pixelDimension(view.bounds.height),
                            cache, address) != 0
            }
        }
        if accepted { hostedViews[ObjectIdentifier(view)] = view }
        return accepted
    }

    func close(view: NSView) {
        precondition(Thread.isMainThread)
        observers.removeValue(forKey: ObjectIdentifier(view))
        guard isAvailable, !terminating, let closeBrowser else { return }
        closingViews[ObjectIdentifier(view)] = view
        hostedViews.removeValue(forKey: ObjectIdentifier(view))
        closeBrowser(Unmanaged.passUnretained(view).toOpaque())
        releaseClosedViews()
    }

    var isInitializedNow: Bool { isInitialized?() == 1 }
    var pendingCloseCount: Int { closingViews.count }
    var isMessagePumpRunning: Bool { pumpTimer != nil }

    func observe(view: NSView, onTick: @escaping () -> Void) {
        guard !terminating else { return }
        observers[ObjectIdentifier(view)] = onTick
    }

    func hasLiveBrowser(in view: NSView) -> Bool {
        hasBrowser?(Unmanaged.passUnretained(view).toOpaque()) == 1
    }

    func browserIsLoading(in view: NSView) -> Bool {
        isLoading?(Unmanaged.passUnretained(view).toOpaque()) == 1
    }

    func shutDown() {
        precondition(Thread.isMainThread)
        guard !terminating else { return }
        terminating = true
        pumpTimer?.invalidate()
        pumpTimer = nil
        observers.removeAll()
        // The bridge closes remaining browsers and runs OnBeforeClose before
        // cef_shutdown. If a close times out, retain its host until process exit.
        shutdown?()
        releaseClosedViews()
    }

    private func pixelDimension(_ value: CGFloat) -> Int32 {
        guard value.isFinite else { return 1 }
        return Int32(min(CGFloat(Int32.max), max(1, value)))
    }

    private func startMessagePump() {
        guard pumpTimer == nil, !terminating else { return }
        let timer = Timer(timeInterval: 1.0 / 60.0, repeats: true) { [self] _ in
            work?()
            releaseClosedViews()
            for observer in Array(observers.values) { observer() }
        }
        RunLoop.main.add(timer, forMode: .common)
        pumpTimer = timer
        terminationObserver = NotificationCenter.default.addObserver(
            forName: NSApplication.willTerminateNotification, object: nil, queue: .main
        ) { [self] _ in
            shutDown()
        }
    }

    private func releaseClosedViews() {
        guard let isBrowserClosed else { return }
        let closed = closingViews.compactMap { key, view in
            isBrowserClosed(Unmanaged.passUnretained(view).toOpaque()) != 0 ? key : nil
        }
        for key in closed { closingViews.removeValue(forKey: key) }
        // Keep even still-mounted hosts alive if shutdown times out while CEF
        // still references them. SwiftUI teardown must not invalidate parents.
        let detached = hostedViews.compactMap { key, view in
            isBrowserClosed(Unmanaged.passUnretained(view).toOpaque()) != 0 ? key : nil
        }
        for key in detached { hostedViews.removeValue(forKey: key) }
    }
}
