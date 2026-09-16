import AppKit
import MightyCore

extension AppStore {
    /// Native window events and local accessibility actions only. Never starts
    /// sharing, connects a remote computer, or submits a model request.
    func verifyHeaderSettingsSmoke(window: NSWindow) async throws -> [String: Any] {
        guard ProcessInfo.processInfo.arguments.contains("--profile"), !hasModal,
              let content = window.contentView, let screen = window.screen,
              !window.styleMask.contains(.fullScreen) else { throw MightyError("제목줄 검증에는 격리된 일반 창이 필요합니다.") }
        var result: [String: Any] = ["passed": false]
        let originalFrame = window.frame
        defer {
            showSettings = false; settingsShowsRemote = false
            window.setFrame(originalFrame, display: true)
            if let data = try? JSONSerialization.data(withJSONObject: result, options: [.prettyPrinted, .sortedKeys]) {
                try? data.write(to: dataDirectory.appendingPathComponent("header-settings-result.json"), options: .atomic)
            }
        }

        result["toolbarHidden"] = window.toolbar?.isVisible != true
        let windowButtons: [NSWindow.ButtonType] = [.closeButton, .miniaturizeButton, .zoomButton]
        result["nativeWindowButtonsVisible"] = windowButtons.allSatisfy {
            window.standardWindowButton($0).map { !$0.isHidden && $0.window === window } == true
        }
        guard result["toolbarHidden"] as? Bool == true, result["nativeWindowButtonsVisible"] as? Bool == true else {
            throw MightyError("상단 툴바 숨김 또는 macOS 창 버튼 유지를 확인하지 못했습니다.")
        }

        let visible = screen.visibleFrame
        let width = min(1100, max(window.minSize.width, visible.width - 100))
        let height = min(760, max(window.minSize.height, visible.height - 100))
        window.setFrame(NSRect(x: visible.minX + 30, y: visible.minY + 30, width: width, height: height), display: true)
        window.makeKeyAndOrderFront(nil)
        try await Task.sleep(for: .milliseconds(180))
        content.layoutSubtreeIfNeeded()
        let normalFrame = window.frame
        try headerSmokeDoubleClick(window)
        try await waitForSmoke(timeout: 3) { !self.headerFramesEqual(window.frame, normalFrame) }
        result["zoomFrame"] = NSStringFromRect(window.frame)
        result["doubleClickZoom"] = !window.styleMask.contains(.fullScreen)
        guard !window.styleMask.contains(.fullScreen) else { throw MightyError("제목줄 두 번 클릭이 전체 화면으로 전환되었습니다.") }
        try await Task.sleep(for: .milliseconds(220))
        try headerSmokeDoubleClick(window)
        try await waitForSmoke(timeout: 3) { self.headerFramesEqual(window.frame, normalFrame) }
        result["doubleClickRestoresFrame"] = true
        result["titleHitTargetVerified"] = true
        result["normalFrame"] = NSStringFromRect(normalFrame)
        result["headerScreenshot"] = try captureSmokeWindow(window, filename: "workspace-header.png").path

        guard let settings = headerSmokeElement(window, label: "설정"), headerSmokePress(settings) else {
            throw MightyError("사이드바 설정 버튼의 실제 동작을 실행하지 못했습니다.")
        }
        try await waitForSmoke(timeout: 3) { self.showSettings && window.attachedSheet != nil }
        guard let sheet = window.attachedSheet else { throw MightyError("설정 창이 없습니다.") }
        try await waitForSmoke(timeout: 3) { self.headerSmokeElement(sheet, identifier: "settings-remote") != nil }
        try await Task.sleep(for: .milliseconds(150))
        let settingsSize = sheet.frame.size
        result["settingsFrame"] = NSStringFromRect(sheet.frame)
        guard let remote = headerSmokeElement(sheet, identifier: "settings-remote"), headerSmokePress(remote) else {
            throw MightyError("설정의 Tailscale 버튼을 실행하지 못했습니다.")
        }
        try await waitForSmoke(timeout: 3) { self.settingsShowsRemote && self.headerSmokeElement(sheet, label: "설정으로 돌아가기") != nil }
        guard !showRemote, window.attachedSheet === sheet else { throw MightyError("원격 설정이 중복 시트로 열렸습니다.") }
        try await waitForSmoke(timeout: 3) { sheet.frame.width > settingsSize.width + 100 }
        try await Task.sleep(for: .milliseconds(150))
        result["remoteSettingsFrame"] = NSStringFromRect(sheet.frame)
        result["remoteSettingsResizesSheet"] = true
        result["settingsGearOpensSheet"] = true
        result["remoteSettingsUsesSameSheet"] = true
        result["remoteSettingsScreenshot"] = try captureSmokeWindow(sheet, filename: "remote-settings.png").path
        guard let back = headerSmokeElement(sheet, label: "설정으로 돌아가기"), headerSmokePress(back) else {
            throw MightyError("원격 설정에서 일반 설정으로 돌아가지 못했습니다.")
        }
        try await waitForSmoke(timeout: 3) { !self.settingsShowsRemote && self.headerSmokeElement(sheet, identifier: "settings-remote") != nil }
        guard showSettings, window.attachedSheet === sheet else { throw MightyError("돌아가기에서 설정 창 전체가 닫혔습니다.") }
        try await waitForSmoke(timeout: 3) { abs(sheet.frame.width - settingsSize.width) < 2 && abs(sheet.frame.height - settingsSize.height) < 2 }
        try await Task.sleep(for: .milliseconds(150))
        result["restoredSettingsFrame"] = NSStringFromRect(sheet.frame)
        result["settingsRestoresSheetSize"] = true
        guard let close = headerSmokeElement(sheet, label: "닫기"), headerSmokePress(close) else { throw MightyError("설정 창 닫기 버튼을 실행하지 못했습니다.") }
        try await waitForSmoke(timeout: 3) { !self.showSettings && window.attachedSheet == nil }
        guard !settingsShowsRemote, !showRemote else { throw MightyError("설정 닫기 후 원격 설정 상태가 남았습니다.") }
        result["remoteBackAndClose"] = true
        result["passed"] = true
        return result
    }

    private func headerFramesEqual(_ lhs: NSRect, _ rhs: NSRect) -> Bool {
        abs(lhs.minX - rhs.minX) < 2 && abs(lhs.minY - rhs.minY) < 2 && abs(lhs.width - rhs.width) < 2 && abs(lhs.height - rhs.height) < 2
    }

    private func headerSmokeDoubleClick(_ window: NSWindow) throws {
        guard let content = window.contentView else { throw MightyError("제목줄의 창이 없습니다.") }
        content.layoutSubtreeIfNeeded()
        func regions(_ view: NSView) -> [WorkspaceTitlebarView] {
            ((view as? WorkspaceTitlebarView).map { [$0] } ?? []) + view.subviews.flatMap(regions)
        }
        guard let region = regions(content).filter({ $0.window === window && !$0.paneDockVisibleRect.isEmpty }).max(by: { $0.paneDockVisibleRect.width < $1.paneDockVisibleRect.width }) else {
            throw MightyError("실제 워크스페이스 제목줄 영역이 없습니다.")
        }
        let rect = region.paneDockVisibleRect
        let location = region.convert(NSPoint(x: rect.midX, y: rect.midY), to: nil)
        guard content.hitTest(content.superview?.convert(location, from: nil) ?? location) === region else {
            throw MightyError("제목줄 마우스 입력이 네이티브 창 드래그 영역에 도달하지 않습니다.")
        }
        let types: [NSEvent.EventType] = [.leftMouseDown, .leftMouseUp]
        for type in types {
            guard let event = NSEvent.mouseEvent(with: type, location: location, modifierFlags: [], timestamp: ProcessInfo.processInfo.systemUptime,
                windowNumber: window.windowNumber, context: nil, eventNumber: 1, clickCount: 2, pressure: type == .leftMouseDown ? 1 : 0) else {
                throw MightyError("제목줄 마우스 검증 이벤트를 만들지 못했습니다.")
            }
            window.sendEvent(event)
        }
    }

    private func headerSmokeElement(_ element: Any, identifier: String? = nil, label: String? = nil, depth: Int = 0) -> NSObject? {
        guard depth < 30, let object = element as? NSObject else { return nil }
        func value(_ key: String) -> Any? { object.responds(to: NSSelectorFromString(key)) ? object.value(forKey: key) : nil }
        let role = value("accessibilityRole") as? String
        let id = value("accessibilityIdentifier") as? String
        let names = [value("accessibilityLabel") as? String, value("accessibilityTitle") as? String].compactMap { $0 }
        if role == NSAccessibility.Role.button.rawValue,
           (identifier != nil && id == identifier || label.map(names.contains) == true) { return object }
        for child in value("accessibilityChildren") as? [Any] ?? [] {
            if let found = headerSmokeElement(child, identifier: identifier, label: label, depth: depth + 1) { return found }
        }
        return nil
    }

    private func headerSmokePress(_ object: NSObject) -> Bool {
        if let accessible = object as? any NSAccessibilityProtocol { return accessible.accessibilityPerformPress() }
        let selector = NSSelectorFromString("accessibilityPerformPress")
        guard object.responds(to: selector), let implementation = object.method(for: selector) else { return false }
        typealias Press = @convention(c) (AnyObject, Selector) -> Bool
        return unsafeBitCast(implementation, to: Press.self)(object, selector)
    }
}
