import AppKit
import Darwin
import MightyCore

// One pane owns its host view and navigation state. The process runtime owns
// the loaded bridge, CEF initialization, message pump, and asynchronous closes.
final class CefBrowserEngine: NSObject, BrowserEngine, ObservableObject, @unchecked Sendable {
    @Published private(set) var navState = BrowserNavigationState()
    @Published private(set) var failureReason: String?

    let profilePath: URL
    private let hostView: BrowserHostView
    private let runtime = CefBrowserRuntime.shared
    private var history = BrowserHistory()
    private var engineReady = false
    private var pendingNavigation = false

    init(profileKey: String, profileDirectory: URL? = nil) {
        hostView = BrowserHostView(frame: .zero)
        profilePath = Self.browserProfilesRoot(profileDirectory: profileDirectory)
            .appendingPathComponent(profileKey, isDirectory: true)
        super.init()
        do {
            try FileManager.default.createDirectory(at: profilePath, withIntermediateDirectories: true)
        } catch {
            failureReason = L("browser.engine.failed")
            return
        }
        // Never delete Chromium locks here: another tab can be using this same
        // workspace profile. Chromium handles genuine stale locks itself.
        openBridge()
        hostView.onAttached = { [weak self] in self?.presentWhenAttached() }
        runtime.observe(view: hostView) { [weak self] in self?.refreshLoadingState() }
    }

    static func browserProfilesRoot(profileDirectory: URL? = nil) -> URL {
        let arguments = ProcessInfo.processInfo.arguments
        let profileArgument = arguments.firstIndex(of: "--profile").flatMap {
            arguments.indices.contains($0 + 1) ? arguments[$0 + 1] : nil
        }
        if let directory = profileDirectory ?? profileArgument.map({ URL(fileURLWithPath: $0) }) {
            return directory.appendingPathComponent("browser-profiles", isDirectory: true)
        }
        return BrowserProfileSupport.profilePath(workspaceProfileKey: "")
    }

    /// Chromium runs inside the app process, so a Chromium crash takes every
    /// agent session down with it. It only starts when the user opted in.
    static let enabledDefaultsKey = "browser.engineEnabled"
    static var isEnabledInSettings: Bool {
        get { UserDefaults.standard.bool(forKey: enabledDefaultsKey) }
        set { UserDefaults.standard.set(newValue, forKey: enabledDefaultsKey) }
    }
    /// Read once at launch: CEF can only initialize before the run loop starts.
    private(set) static var enabledAtLaunch = false

    /// Must run before App.main(), never on first browser pane creation.
    static func bootstrapRuntime() {
        enabledAtLaunch = isEnabledInSettings || ProcessInfo.processInfo.arguments.contains("--browser-smoke-test")
        guard enabledAtLaunch,
              case .available(let frameworkPath) = BrowserEngineLocator.locate(),
              let frameworks = Bundle.main.privateFrameworksPath else { return }
        let cacheRoot = browserProfilesRoot()
        do {
            try FileManager.default.createDirectory(at: cacheRoot, withIntermediateDirectories: true)
        } catch { return }
        let runtime = CefBrowserRuntime.shared
        guard runtime.load(
            frameworkBinary: frameworkPath.appendingPathComponent("Chromium Embedded Framework"),
            bridgePath: "\(frameworks)/MightyCEFBridge.dylib"
        ) else { return }
        _ = runtime.initialize(cacheRoot: cacheRoot)
    }

    deinit {
        let view = hostView
        // SwiftUI can release state off the main thread. Keep the host alive
        // while dispatching close; the runtime then retains it through CEF's
        // asynchronous OnBeforeClose callback.
        if Thread.isMainThread {
            runtime.close(view: view)
        } else {
            DispatchQueue.main.async { CefBrowserRuntime.shared.close(view: view) }
        }
    }

    var containerView: NSView { hostView }
    var hasLiveBrowser: Bool { runtime.hasLiveBrowser(in: hostView) }
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

    func reload() { present(history.current) }

    private func presentWhenAttached() {
        guard engineReady, hostView.window != nil else { return }
        if history.current == nil {
            history.visit(URL(string: "about:blank")!)
        }
        // A layout/tab remount should not reload an already running browser.
        if pendingNavigation || !hasLiveBrowser { present(history.current) }
    }

    private func present(_ url: URL?) {
        guard let url else { return }
        navState = history.state(isLoading: true)
        guard engineReady else {
            navState = history.state(isLoading: false)
            return
        }
        // CEF needs an attached parent view. Keep early navigation in history
        // and submit it when AppKit attaches the host to a window.
        guard hostView.window != nil else {
            pendingNavigation = true
            return
        }
        pendingNavigation = false
        if runtime.show(in: hostView, profile: profilePath, url: url) {
            failureReason = nil
        } else {
            failureReason = L("browser.engine.failed")
            navState = history.state(isLoading: false)
        }
    }

    private func refreshLoadingState() {
        guard hasLiveBrowser else { return }
        let next = history.state(isLoading: runtime.browserIsLoading(in: hostView))
        if next != navState { navState = next }
    }

    private func openBridge() {
        guard Self.enabledAtLaunch else {
            failureReason = L("browser.engine.disabled")
            return
        }
        switch BrowserEngineLocator.locate() {
        case .missing(let reason):
            failureReason = reason
        case .available:
            engineReady = runtime.isAvailable && runtime.isInitializedNow
            if !engineReady { failureReason = L("browser.engine.failed") }
        }
    }
}

// Resizing the pane resizes the browser: CEF parents its own NSView here, so
// the host keeps every child filling its bounds.
final class BrowserHostView: NSView {
    var onAttached: (() -> Void)?

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        if window != nil {
            // Finish the AppKit/SwiftUI mounting transaction before CEF inserts
            // its child view or publishes observable navigation state.
            DispatchQueue.main.async { [weak self] in
                guard let self, self.window != nil else { return }
                self.onAttached?()
            }
        }
    }

    override var isFlipped: Bool { true }

    override func resizeSubviews(withOldSize oldSize: NSSize) {
        for subview in subviews { subview.frame = bounds }
    }

    override func layout() {
        super.layout()
        for subview in subviews { subview.frame = bounds }
    }
}
