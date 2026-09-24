import AppKit
import Darwin
import MightyCore
import SwiftUI

extension AppStore {
    /// A real windowed CEF session with local HTML and JavaScript completion
    /// beacons. Uses an isolated --profile; no model or external site is called.
    func runBrowserSmokeTest() async {
        let args = ProcessInfo.processInfo.arguments
        guard args.contains("--profile") else { error = "브라우저 검증에는 임시 --profile이 필요합니다."; return }
        var report: [String: Any] = ["passed": false, "windowed": true, "aiRequestSent": false, "usesTestKeychain": args.contains("--use-mock-keychain")]
        let requests = BrowserSmokeRequests()
        let server = HTTPServer(address: "127.0.0.1", port: 0) { request in
            await requests.record(request.target)
            if request.target.hasPrefix("/rendered/") { return HTTPResponse(status: 204, body: Data()) }
            let name = request.target == "/one" ? "one" : "two"
            let color = name == "one" ? "#176b47" : "#244ca0"
            let html = """
            <!doctype html><meta charset="utf-8"><title>Browser \(name)</title>
            <style>body{background:\(color);color:white;font:28px system-ui;padding:32px}button{font:20px system-ui}</style>
            <h1>MightyClaude browser \(name)</h1><p>페이지 렌더링 · JavaScript 실행 완료</p>
            <button onclick="document.body.append(' clicked')">실행 확인</button>
            <script>fetch('/loaded/\(name)',{cache:'no-store'});requestAnimationFrame(()=>requestAnimationFrame(()=>fetch('/rendered/\(name)',{cache:'no-store'})));</script>
            """
            return HTTPResponse(status: 200, body: Data(html.utf8), headers: ["Content-Type": "text/html; charset=utf-8", "Cache-Control": "no-store"])
        }
        let window = NSWindow(contentRect: NSRect(x: 80, y: 120, width: 1080, height: 700),
                              styleMask: [.titled, .closable, .resizable], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        window.title = "MightyClaude 브라우저 실행 검증"
        var engines: [CefBrowserEngine] = []
        func mount(_ values: [CefBrowserEngine]) {
            window.contentView = NSHostingView(rootView: HStack(spacing: 2) {
                ForEach(Array(values.enumerated()), id: \.offset) { _, engine in BrowserContainerView(engine: engine) }
            })
            window.makeKeyAndOrderFront(nil)
            NSApp.activate(ignoringOtherApps: true)
        }
        do {
            try FileManager.default.createDirectory(at: dataDirectory, withIntermediateDirectories: true)
            report["applicationClass"] = NSStringFromClass(type(of: NSApp!))
            guard NSApp is MightyApplication else { throw MightyError("CEF 애플리케이션 클래스가 적용되지 않았습니다.") }
            let port = try await server.start()
            let firstURL = URL(string: "http://127.0.0.1:\(port)/one")!
            let secondURL = URL(string: "http://127.0.0.1:\(port)/two")!

            // The original crash called a dlclosed bridge after a short-lived
            // SwiftUI engine disappeared. Exercise that lifetime first.
            for _ in 0..<4 { autoreleasepool { _ = CefBrowserEngine(profileKey: "transient") } }
            try await Task.sleep(for: .milliseconds(180))
            report["unmountedEnginesCanBeReleased"] = true
            guard CefBrowserRuntime.shared.isInitializedNow && CefBrowserRuntime.shared.isMessagePumpRunning else { throw MightyError("이벤트 루프 시작 전 CEF 초기화가 완료되지 않았습니다.") }
            report["engineInitializedBeforeRunLoop"] = true

            engines = [CefBrowserEngine(profileKey: "browser-smoke-a"), CefBrowserEngine(profileKey: "browser-smoke-b")]
            mount(engines)
            engines[0].loadURL(firstURL); engines[1].loadURL(secondURL)
            try await waitForSmoke(timeout: 25) {
                requests.count("/rendered/one") > 0 && requests.count("/rendered/two") > 0
                    && engines.allSatisfy { $0.hasLiveBrowser && !$0.navState.isLoading }
            }
            report["twoPanesRenderJavaScript"] = true
            report["independentProfiles"] = engines[0].profilePath != engines[1].profilePath
            report["screenshot"] = try captureSmokeWindow(window, filename: "browser-two-panes.png").path

            let beforeReload = requests.count("/rendered/one")
            engines[0].reload()
            try await waitForSmoke(timeout: 10) { requests.count("/rendered/one") > beforeReload }
            let beforeNavigate = requests.count("/rendered/two")
            engines[0].loadURL(secondURL)
            try await waitForSmoke(timeout: 10) { requests.count("/rendered/two") > beforeNavigate }
            let beforeBack = requests.count("/rendered/one")
            engines[0].goBack()
            try await waitForSmoke(timeout: 10) { requests.count("/rendered/one") > beforeBack && engines[0].canGoForward }
            let beforeForward = requests.count("/rendered/two")
            engines[0].goForward()
            try await waitForSmoke(timeout: 10) { requests.count("/rendered/two") > beforeForward }
            report["reloadBackForward"] = true

            weak var closedEngine = engines[1]
            engines.removeLast(); mount(engines)
            do {
                try await waitForSmoke(timeout: 10) { closedEngine == nil && CefBrowserRuntime.shared.pendingCloseCount == 0 }
            } catch {
                report["closedEngineReleased"] = closedEngine == nil
                report["pendingBrowserCloses"] = CefBrowserRuntime.shared.pendingCloseCount
                throw error
            }
            let survivingReload = requests.count("/rendered/two")
            engines[0].reload()
            try await waitForSmoke(timeout: 10) { requests.count("/rendered/two") > survivingReload }
            report["closingOnePaneKeepsOtherAlive"] = true
            window.contentView = NSView(); engines.removeAll()
            try await waitForSmoke(timeout: 10) { CefBrowserRuntime.shared.pendingCloseCount == 0 }
            try await Task.sleep(for: .milliseconds(300))
            report["lastPaneCloseDoesNotUnloadPump"] = true

            // Exceed the native bridge slot count to catch unreclaimed pane slots.
            for index in 0..<35 {
                let before = requests.count("/rendered/one")
                engines = [CefBrowserEngine(profileKey: "browser-smoke-a")]
                mount(engines); engines[0].loadURL(firstURL)
                try await waitForSmoke(timeout: 15) { requests.count("/rendered/one") > before && engines[0].hasLiveBrowser }
                weak var reopenedEngine = engines[0]
                window.contentView = NSView(); engines.removeAll()
                try await waitForSmoke(timeout: 10) { reopenedEngine == nil && CefBrowserRuntime.shared.pendingCloseCount == 0 }
                report["reopenCycles"] = index + 1
            }
            // Production shutdown runs while SwiftUI still owns visible panes.
            // Check that path as well as the explicit close/reopen cycles.
            let beforeShutdown = requests.count("/rendered/one")
            engines = [CefBrowserEngine(profileKey: "browser-smoke-a")]
            mount(engines); engines[0].loadURL(firstURL)
            try await waitForSmoke(timeout: 15) { requests.count("/rendered/one") > beforeShutdown && engines[0].hasLiveBrowser }
            report["shutdownStartsWithLivePane"] = true
            report["passed"] = true
        } catch {
            report["error"] = error.localizedDescription
            report["requestCounts"] = requests.snapshot
            report["browserStates"] = engines.map { ["live": $0.hasLiveBrowser, "loading": $0.navState.isLoading, "failure": $0.failureReason ?? ""] as [String: Any] }
            report["pendingBrowserClosesAtFailure"] = CefBrowserRuntime.shared.pendingCloseCount
        }
        // Preserve diagnostics even if teardown itself fails during a regression.
        try? JSONSerialization.data(withJSONObject: report, options: [.prettyPrinted, .sortedKeys])
            .write(to: dataDirectory.appendingPathComponent("browser-smoke-result.json"), options: .atomic)
        CefBrowserRuntime.shared.shutDown()
        window.contentView = NSView(); engines.removeAll()
        report["engineShutdownComplete"] = !CefBrowserRuntime.shared.isInitializedNow
        if CefBrowserRuntime.shared.isInitializedNow { report["passed"] = false }
        window.orderOut(nil); window.close()
        await server.stop()
        do {
            try JSONSerialization.data(withJSONObject: report, options: [.prettyPrinted, .sortedKeys])
                .write(to: dataDirectory.appendingPathComponent("browser-smoke-result.json"), options: .atomic)
        } catch { report["passed"] = false; self.error = error.localizedDescription }
        if args.contains("--smoke-exit") {
            await shutdown()
            Darwin.exit(report["passed"] as? Bool == true ? 0 : 1)
        }
    }
}

@MainActor private final class BrowserSmokeRequests {
    private var targets: [String: Int] = [:]
    func record(_ target: String) { targets[target, default: 0] += 1 }
    func count(_ target: String) -> Int { targets[target, default: 0] }
    var snapshot: [String: Int] { targets }
}
