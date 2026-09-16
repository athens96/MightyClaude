import AppKit
import MightyCore
import SwiftUI

@MainActor
enum SessionInfoDiagnostics {
    /// Fixture usage only: no CLI runs, external logs, account data, or clipboard.
    static func run(store: AppStore) async -> [String: Any] {
        var result: [String: Any] = ["passed": false, "aiRequestSent": false]
        guard ProcessInfo.processInfo.arguments.contains("--profile"), !store.hasModal,
              let workspace = store.activeWorkspace else { result["error"] = "세션 정보 진단에는 격리된 워크스페이스가 필요합니다."; return result }
        let previous = store.snapshot
        let previousWindow = NSApp.keyWindow
        let window = NSWindow(contentRect: NSRect(x: 140, y: 120, width: 540, height: 620), styleMask: [.titled, .closable, .resizable], backing: .buffered, defer: false)
        window.title = "세션 정보 검증"
        window.isReleasedWhenClosed = false
        defer {
            store.sessionInfoSessionID = nil
            window.orderOut(nil); window.close()
            store.snapshot = previous
            previousWindow?.makeKeyAndOrderFront(nil)
        }
        do {
            try FileManager.default.createDirectory(at: store.dataDirectory, withIntermediateDirectories: true)
            var measured = RunSession(id: "usage-measured-fixture", workspaceId: workspace.id, title: "측정한 세션", provider: "claude", model: "a-long-saved-model-for-context-geometry", status: "running", resumeId: "fixture-cli-session")
            measured.sessionUsage = SessionUsage(provider: "claude", source: "claude-mods", tokenScope: "run", model: "claude-sonnet-fixture", providerSessionId: "fixture-cli-session", inputTokens: 42_000, outputTokens: 8_000, cacheReadTokens: 20_000, cacheWriteTokens: 4_000, reasoningTokens: 5_000, totalTokens: 50_000, contextUsedTokens: 84_000, contextWindowTokens: 200_000, costUSD: 0.25, costScope: "session")
            measured.runTiming = AgentRunTiming(startedAt: Date().addingTimeInterval(-73))
            let unknown = RunSession(id: "usage-unknown-fixture", workspaceId: workspace.id, title: "미측정 세션", provider: "codex")
            store.snapshot.sessions.append(contentsOf: [measured, unknown])
            let host = NSHostingView(rootView: SessionInfoFixture(store: store, sessionID: measured.id))
            window.contentView = host
            window.makeKeyAndOrderFront(nil)
            var rows: [[String: Any]] = []
            for width in [CGFloat(920), 540, 315] {
                window.setContentSize(NSSize(width: width, height: 620))
                try await Task.sleep(for: .milliseconds(150))
                guard let context = node(window, identifier: "context-\(measured.id)"), let send = node(window, identifier: "composer-stop-\(measured.id)") else { throw MightyError("컨텍스트 또는 보내기 버튼을 찾지 못했습니다.") }
                let content = window.convertToScreen(window.contentView?.convert(window.contentView?.bounds ?? .zero, to: nil) ?? .zero)
                let controls = ["attach-", "composer-model-", "composer-options-", "composer-effort-", "composer-permission-", "composer-more-", "composer-stop-", "context-", "send-"].compactMap { node(window, identifier: $0 + measured.id) }
                let aligned = abs(context.frame.height - 32) <= 2 && abs(send.frame.height - 32) <= 2 && abs(context.frame.midY - send.frame.midY) <= 1 && abs(send.frame.minX - context.frame.maxX - 6) <= 1
                let inside = controls.allSatisfy { $0.frame.minX >= content.minX - 1 && $0.frame.maxX <= content.maxX + 1 && abs($0.frame.midY - send.frame.midY) <= 1 }
                rows.append(["width": width, "aligned": aligned, "insideWindow": inside, "contextFrame": NSStringFromRect(context.frame), "sendFrame": NSStringFromRect(send.frame)])
                guard aligned && inside else { throw MightyError("컨텍스트 버튼이 보내기 왼쪽 한 줄에 맞지 않습니다.") }
            }
            result["controlGeometry"] = rows
            result["contextImmediatelyLeftOfPrimaryAction"] = true
            result["narrowScreenshot"] = try store.captureSmokeWindow(window, filename: "session-context-narrow.png").path
            window.setContentSize(NSSize(width: 540, height: 620))
            try await Task.sleep(for: .milliseconds(100))
            guard let context = node(window, identifier: "context-\(measured.id)"), press(context.object) else { throw MightyError("컨텍스트 상세 버튼을 누르지 못했습니다.") }
            try await store.waitForSmoke(timeout: 3) {
                store.sessionInfoSessionID == measured.id && popup(sessionID: measured.id) != nil
            }
            guard let measuredPopup = popup(sessionID: measured.id),
                  node(measuredPopup, identifier: "session-info-context-\(measured.id)")?.text.contains("42%") == true,
                  node(measuredPopup, identifier: "session-info-model-\(measured.id)")?.text.contains("claude-sonnet-fixture") == true,
                  node(measuredPopup, identifier: "session-info-cost-\(measured.id)")?.text.contains("이 대화 누적") == true else { throw MightyError("측정한 세션의 컨텍스트·모델·비용 범위가 표시되지 않았습니다.") }
            result["knownUsageVisible"] = true
            let knownHeight = try await settledHeight(of: measuredPopup, store: store)
            result["knownWindowHeight"] = knownHeight
            result["knownScreenGeometry"] = screenGeometry(of: measuredPopup)
            result["knownAnchorGeometry"] = anchorGeometry(of: measuredPopup, window: window, sessionID: measured.id)
            result["measuredScreenshot"] = try store.captureSmokeWindow(measuredPopup, filename: "session-info-measured.png").path
            guard withinVisibleScreen(measuredPopup) else { throw MightyError("측정 세션 팝오버가 화면 밖에 표시되었습니다.") }
            guard result["knownAnchorGeometry"].flatMap({ $0 as? [String: Any] })?["attached"] as? Bool == true else { throw MightyError("측정 세션 팝오버가 입력 버튼에서 떨어져 있습니다.") }
            guard node(measuredPopup, identifier: "session-info-identity-\(measured.id)") == nil,
                  let identifiers = node(measuredPopup, identifier: "session-info-identifiers-\(measured.id)"), press(identifiers.object) else { throw MightyError("접힌 세션 ID를 펼칠 수 없습니다.") }
            result["identifiersInitiallyCollapsed"] = true
            try await store.waitForSmoke(timeout: 3) {
                node(measuredPopup, identifier: "session-info-identity-\(measured.id)")?.text.contains(measured.id) == true &&
                node(measuredPopup, identifier: "session-info-cli-identity-\(measured.id)")?.text.contains("fixture-cli-session") == true
            }
            let expandedKnownHeight = try await settledHeight(of: measuredPopup, store: store)
            result["expandedKnownWindowHeight"] = expandedKnownHeight
            result["expandedKnownScreenGeometry"] = screenGeometry(of: measuredPopup)
            result["expandedKnownAnchorGeometry"] = anchorGeometry(of: measuredPopup, window: window, sessionID: measured.id)
            result["identifiersAccessible"] = true
            result["expandedScreenshot"] = try store.captureSmokeWindow(measuredPopup, filename: "session-info-identifiers.png").path
            guard withinVisibleScreen(measuredPopup) else { throw MightyError("세션 ID를 펼친 팝오버가 화면 밖으로 벗어났습니다.") }
            guard result["expandedKnownAnchorGeometry"].flatMap({ $0 as? [String: Any] })?["attached"] as? Bool == true else { throw MightyError("세션 ID를 펼친 팝오버가 입력 버튼에서 떨어져 있습니다.") }
            guard let content = measuredPopup.contentView else { throw MightyError("세션 정보 창의 내용을 찾지 못했습니다.") }
            // SwiftUI AX container frames are the union of descendants and may
            // already exclude padding. Compare against the actual native clip,
            // not a second imagined inset around that accessibility union.
            let clip = scrollClip(in: content)
            let viewport = clip ?? content
            let viewportFrame = measuredPopup.convertToScreen(viewport.convert(viewport.bounds, to: nil))
            result["detailViewportFrame"] = NSStringFromRect(viewportFrame)
            result["detailViewportSource"] = clip == nil ? "window-content" : "scroll-clip"
            // The SwiftUI content caps at 480pt; native popover chrome adds its
            // own insets. The actual scroll viewport remains at most 410pt.
            guard knownHeight <= 520, expandedKnownHeight <= 520,
                  clip == nil || viewportFrame.height <= 411 else { throw MightyError("측정 정보의 팝오버 최대 높이를 초과했습니다.") }
            result["populatedHeightBounded"] = true
            let fields = ["context", "model", "workspace", "path", "input", "output", "cache-read", "cache-write", "reasoning", "total", "cost", "identity", "cli-identity"]
            var fieldGeometry: [[String: Any]] = []
            for field in fields {
                guard let value = node(measuredPopup, identifier: "session-info-\(field)-\(measured.id)") else { throw MightyError("세션 정보 \(field) 필드가 없습니다.") }
                let contained = value.frame.minX >= viewportFrame.minX - 1 && value.frame.maxX <= viewportFrame.maxX + 1
                fieldGeometry.append(["field": field, "insidePopover": contained, "frame": NSStringFromRect(value.frame)])
                guard contained else { result["detailFieldGeometry"] = fieldGeometry; throw MightyError("세션 정보 \(field) 값이 팝오버 가로 범위를 벗어났습니다.") }
            }
            result["detailFieldGeometry"] = fieldGeometry
            result["detailsWithinPopoverWidth"] = true
            store.selectSession(unknown.id)
            guard let index = store.snapshot.sessions.firstIndex(where: { $0.id == measured.id }) else { throw MightyError("측정 세션이 없어졌습니다.") }
            store.snapshot.sessions[index].sessionUsage?.contextUsedTokens = 100_000
            try await store.waitForSmoke(timeout: 3) {
                node(measuredPopup, identifier: "session-info-context-\(measured.id)")?.text.contains("50%") == true
            }
            guard store.sessionInfoSessionID == measured.id else { throw MightyError("활성 창 전환이 상세 대상 세션을 바꿨습니다.") }
            result["requestedSessionRetainedOnActiveChange"] = true
            result["liveUsageRefresh"] = true

            store.sessionInfoSessionID = nil
            try await store.waitForSmoke(timeout: 3) { popup(sessionID: measured.id) == nil }
            host.rootView = SessionInfoFixture(store: store, sessionID: unknown.id)
            try await store.waitForSmoke(timeout: 3) { node(window, identifier: "context-\(unknown.id)") != nil }
            guard let context = node(window, identifier: "context-\(unknown.id)"), context.text.contains("—"), press(context.object) else { throw MightyError("미측정 세션 버튼을 확인하지 못했습니다.") }
            try await store.waitForSmoke(timeout: 3) { store.sessionInfoSessionID == unknown.id && popup(sessionID: unknown.id) != nil }
            guard let unknownPopup = popup(sessionID: unknown.id),
                  node(unknownPopup, identifier: "session-info-context-\(unknown.id)")?.text.contains("—") == true,
                  node(unknownPopup, identifier: "session-info-provider-\(unknown.id)")?.text.contains("Codex") == true,
                  node(unknownPopup, identifier: "session-info-input-\(unknown.id)") == nil,
                  node(unknownPopup, identifier: "session-info-cost-\(unknown.id)") == nil else { throw MightyError("미측정 세션에 다른 세션의 값이나 가짜 비용이 표시되었습니다.") }
            result["unknownUsageNotInvented"] = true
            result["sessionRouting"] = true
            let unknownHeight = try await settledHeight(of: unknownPopup, store: store)
            result["unknownWindowHeight"] = unknownHeight
            result["unknownScreenGeometry"] = screenGeometry(of: unknownPopup)
            result["unknownAnchorGeometry"] = anchorGeometry(of: unknownPopup, window: window, sessionID: unknown.id)
            guard withinVisibleScreen(unknownPopup) else { throw MightyError("미측정 세션 팝오버가 화면 밖에 표시되었습니다.") }
            guard result["unknownAnchorGeometry"].flatMap({ $0 as? [String: Any] })?["attached"] as? Bool == true else { throw MightyError("짧은 세션 팝오버가 입력 버튼에서 떨어져 있습니다.") }
            guard unknownHeight <= 350, unknownHeight + 60 < knownHeight else { throw MightyError("미측정 세션 팝오버에 불필요한 세로 공간이 남았습니다.") }
            result["unknownHeightFitsContent"] = true
            result["unknownScreenshot"] = try store.captureSmokeWindow(unknownPopup, filename: "session-info-unknown.png").path
            guard let unknownIdentifiers = node(unknownPopup, identifier: "session-info-identifiers-\(unknown.id)"), press(unknownIdentifiers.object) else { throw MightyError("미측정 세션 ID를 펼칠 수 없습니다.") }
            try await store.waitForSmoke(timeout: 3) { node(unknownPopup, identifier: "session-info-identity-\(unknown.id)") != nil }
            let expandedUnknownHeight = try await settledHeight(of: unknownPopup, store: store)
            result["expandedUnknownScreenGeometry"] = screenGeometry(of: unknownPopup)
            result["expandedUnknownAnchorGeometry"] = anchorGeometry(of: unknownPopup, window: window, sessionID: unknown.id)
            guard withinVisibleScreen(unknownPopup) else { throw MightyError("미측정 세션 ID를 펼친 팝오버가 화면 밖으로 벗어났습니다.") }
            guard result["expandedUnknownAnchorGeometry"].flatMap({ $0 as? [String: Any] })?["attached"] as? Bool == true else { throw MightyError("펼친 미측정 팝오버가 입력 버튼에서 떨어져 있습니다.") }
            guard expandedUnknownHeight >= unknownHeight + 10,
                  let expandedIdentifiers = node(unknownPopup, identifier: "session-info-identifiers-\(unknown.id)"), press(expandedIdentifiers.object) else { throw MightyError("세션 ID 펼침에 맞춰 팝오버가 늘어나지 않았습니다.") }
            try await store.waitForSmoke(timeout: 3) { node(unknownPopup, identifier: "session-info-identity-\(unknown.id)") == nil }
            let collapsedUnknownHeight = try await settledHeight(of: unknownPopup, store: store)
            result["collapsedUnknownScreenGeometry"] = screenGeometry(of: unknownPopup)
            result["collapsedUnknownAnchorGeometry"] = anchorGeometry(of: unknownPopup, window: window, sessionID: unknown.id)
            guard withinVisibleScreen(unknownPopup) else { throw MightyError("세션 ID를 접은 팝오버가 화면 밖으로 벗어났습니다.") }
            guard result["collapsedUnknownAnchorGeometry"].flatMap({ $0 as? [String: Any] })?["attached"] as? Bool == true else { throw MightyError("접은 세션 팝오버가 입력 버튼에서 떨어져 있습니다.") }
            guard abs(collapsedUnknownHeight - unknownHeight) <= 2 else { throw MightyError("세션 ID를 접은 뒤 팝오버가 원래 높이로 줄지 않았습니다.") }
            result["expandedUnknownWindowHeight"] = expandedUnknownHeight
            result["collapsedUnknownWindowHeight"] = collapsedUnknownHeight
            result["heightAdaptsToDisclosure"] = true
            result["popoverRemainsOnScreen"] = true
            result["popoverRemainsAttachedToButton"] = true
            // Queue a live update while dismissing through the real transient
            // popover outside-click path; a pending resize must not reopen it.
            store.objectWillChange.send()
            let outside = NSPoint(x: 20, y: (window.contentView?.bounds.height ?? 620) - 16)
            guard let down = NSEvent.mouseEvent(with: .leftMouseDown, location: outside, modifierFlags: [], timestamp: ProcessInfo.processInfo.systemUptime, windowNumber: window.windowNumber, context: nil, eventNumber: 1, clickCount: 1, pressure: 1),
                  let up = NSEvent.mouseEvent(with: .leftMouseUp, location: outside, modifierFlags: [], timestamp: ProcessInfo.processInfo.systemUptime, windowNumber: window.windowNumber, context: nil, eventNumber: 2, clickCount: 1, pressure: 0) else { throw MightyError("팝오버 닫힘 검증 이벤트를 만들지 못했습니다.") }
            NSApp.postEvent(down, atStart: false)
            NSApp.postEvent(up, atStart: false)
            try await store.waitForSmoke(timeout: 3) { store.sessionInfoSessionID == nil && popup(sessionID: unknown.id) == nil }
            store.objectWillChange.send()
            try await Task.sleep(for: .milliseconds(80))
            guard store.sessionInfoSessionID == nil && popup(sessionID: unknown.id) == nil else { throw MightyError("닫은 팝오버가 갱신 중 다시 열렸습니다.") }
            result["dismissalSurvivesLiveUpdate"] = true
            result["passed"] = true
        } catch {
            result["error"] = error.localizedDescription
            let visibleDetails = store.sessionInfoSessionID.flatMap { popup(sessionID: $0) }
            if let image = try? store.captureSmokeWindow(visibleDetails ?? window, filename: "session-info-failure.png") { result["failureScreenshot"] = image.path }
        }
        return result
    }

    private static func anchorGeometry(of popup: NSWindow, window: NSWindow, sessionID: String) -> [String: Any] {
        guard let anchor = node(window, identifier: "context-\(sessionID)") else { return ["attached": false, "error": "컨텍스트 버튼을 찾지 못했습니다."] }
        let dx = max(0, max(anchor.frame.minX - popup.frame.maxX, popup.frame.minX - anchor.frame.maxX))
        let dy = max(0, max(anchor.frame.minY - popup.frame.maxY, popup.frame.minY - anchor.frame.maxY))
        let gap = (dx * dx + dy * dy).squareRoot()
        return ["anchorFrame": NSStringFromRect(anchor.frame), "popupFrame": NSStringFromRect(popup.frame),
                "gap": gap, "attached": gap <= 24 && (dx <= 1 || dy <= 1)]
    }

    private static func withinVisibleScreen(_ window: NSWindow) -> Bool {
        guard let screen = window.screen ?? NSScreen.main else { return false }
        return screen.visibleFrame.insetBy(dx: -1, dy: -1).contains(window.frame)
    }

    private static func screenGeometry(of window: NSWindow) -> [String: Any] {
        ["windowFrame": NSStringFromRect(window.frame),
         "visibleScreenFrame": NSStringFromRect((window.screen ?? NSScreen.main)?.visibleFrame ?? .zero),
         "contained": withinVisibleScreen(window)]
    }

    private static func settledHeight(of window: NSWindow, store: AppStore) async throws -> CGFloat {
        var previous: CGFloat = -1
        var stableSamples = 0
        try await store.waitForSmoke(timeout: 3) {
            let current = window.contentView?.bounds.height ?? 0
            stableSamples = current > 100 && abs(current - previous) <= 0.5 ? stableSamples + 1 : 0
            previous = current
            return stableSamples >= 3
        }
        return previous
    }

    private static func scrollClip(in view: NSView) -> NSView? {
        if let scroll = view as? NSScrollView, !scroll.isHiddenOrHasHiddenAncestor,
           scroll.contentView.bounds.width > 100, scroll.contentView.bounds.height > 100 { return scroll.contentView }
        return view.subviews.lazy.compactMap { scrollClip(in: $0) }.first
    }

    private struct Node { let object: NSObject; let frame: NSRect; let text: String }
    private static func node(_ element: Any, identifier: String, depth: Int = 0) -> Node? {
        guard depth < 45, let object = element as? NSObject else { return nil }
        func value(_ key: String) -> Any? { object.responds(to: NSSelectorFromString(key)) ? object.value(forKey: key) : nil }
        if value("accessibilityIdentifier") as? String == identifier,
           let frame = (value("accessibilityFrame") as? NSValue)?.rectValue, frame.width > 0, frame.height > 0 {
            let text = [value("accessibilityLabel") as? String, value("accessibilityTitle") as? String, value("accessibilityValue") as? String].compactMap { $0 }.joined(separator: " ")
            return Node(object: object, frame: frame, text: text)
        }
        for child in value("accessibilityChildren") as? [Any] ?? [] {
            if let found = node(child, identifier: identifier, depth: depth + 1) { return found }
        }
        return nil
    }
    private static func popup(sessionID: String) -> NSWindow? {
        NSApp.windows.first { $0.isVisible && node($0, identifier: "session-info-\(sessionID)") != nil }
    }
    private static func press(_ object: NSObject) -> Bool {
        let selector = NSSelectorFromString("accessibilityPerformPress")
        guard object.responds(to: selector), let implementation = object.method(for: selector) else { return false }
        typealias Press = @convention(c) (AnyObject, Selector) -> Bool
        _ = unsafeBitCast(implementation, to: Press.self)(object, selector)
        return true // The caller verifies the actual state and visible popover.
    }
    private struct SessionInfoFixture: View {
        @ObservedObject var store: AppStore
        let sessionID: String
        var body: some View {
            if let session = store.snapshot.sessions.first(where: { $0.id == sessionID }) { SessionPaneView(session: session).environmentObject(store).id(sessionID) }
        }
    }
}
