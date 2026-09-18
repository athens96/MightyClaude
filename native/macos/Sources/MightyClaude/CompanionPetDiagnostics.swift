import AppKit
import MightyCore
import SwiftUI

@MainActor
enum CompanionPetDiagnostics {
    /// App-local pointer events exercise the real monitor and a separate pet
    /// panel. This companion has no store, saved preferences or notification client.
    static func run(store: AppStore) async -> [String: Any] {
        var result: [String: Any] = ["passed": false, "aiRequestSent": false, "appLocalSyntheticPointer": true]
        guard ProcessInfo.processInfo.arguments.contains("--profile") else {
            result["error"] = "펫 이동 검증에는 격리된 프로필이 필요합니다."; return result
        }
        let companion = AgentCompanion()
        let workspace = Workspace(id: "pet-motion-workspace", name: "펫 동작 검증", path: store.dataDirectory.path)
        let session = RunSession(id: "pet-motion-session", workspaceId: workspace.id, title: "펫 동작 검증", status: "running", logs: [LogEntry(kind: "user", text: "문서를 읽고 변경을 검증해 줘.")])
        var snapshot = AppSnapshot(workspaces: [workspace], sessions: [session])
        let visible = NSScreen.main?.visibleFrame ?? NSRect(x: 0, y: 0, width: 1200, height: 800)
        let panel = NSPanel(contentRect: NSRect(x: visible.midX - 141, y: visible.minY + 60, width: 282, height: CompanionPanel.baseHeight), styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.isReleasedWhenClosed = false; panel.isMovableByWindowBackground = false
        panel.isOpaque = false; panel.backgroundColor = .clear; panel.hidesOnDeactivate = false
        panel.becomesKeyOnlyIfNeeded = true; panel.level = .floating
        let previousKeyWindow = NSApp.keyWindow
        var probe: CompanionPetInteractionProbe?
        var stage = "policy"
        defer {
            CompanionPetInteraction.cancel(in: panel.contentView)
            panel.contentView = nil; panel.orderOut(nil); panel.close(); companion.shutdown()
        }
        do {
            try FileManager.default.createDirectory(at: store.dataDirectory, withIntermediateDirectories: true)
            func require(_ value: Bool, _ message: String) throws { guard value else { throw MightyError(message) } }
            let policies = policyChecks()
            result["taskPolicy"] = policies
            try require(policies.values.allSatisfy { $0 }, "작업 상태와 펫 동작의 연결이 올바르지 않습니다.")
            let lifetime = CompanionPetMotion()
            lifetime.begin(); lifetime.move(horizontalDelta: 10); lifetime.endAfterTeardown()
            lifetime.begin(); lifetime.move(horizontalDelta: -10)
            await withCheckedContinuation { continuation in DispatchQueue.main.async { continuation.resume() } }
            try require(lifetime.isDragging && lifetime.direction == .walkLeft, "이전 뷰의 정리가 새 드래그를 취소했습니다.")
            lifetime.endAfterTeardown()
            await withCheckedContinuation { continuation in DispatchQueue.main.async { continuation.resume() } }
            try require(!lifetime.isDragging && lifetime.direction == nil, "뷰 정리 후 펫 이동 상태가 남았습니다.")
            result["deferredTeardownPreservesNewGesture"] = true
            companion.reloadPets(); companion.refresh(snapshot)
            guard let pet = companion.selectedPet else { throw MightyError("기본 펫 프레임을 찾지 못했습니다.") }
            try require(pet.frames.map(\.count) == CompanionPet.frameCounts, "걷기 프레임 수가 올바르지 않습니다.")
            for row in [CompanionPetAnimation.walkRight.rawValue, CompanionPetAnimation.walkLeft.rawValue] {
                guard let first = pet.frame(row: row, elapsed: 0, reducedMotion: true),
                      let later = pet.frame(row: row, elapsed: 0.2, reducedMotion: true),
                      let moving = pet.frame(row: row, elapsed: 0.2, reducedMotion: false) else { throw MightyError("걷기 이미지를 불러오지 못했습니다.") }
                try require(first === later && first !== moving, "동작 줄이기 또는 걷기 프레임 재생이 잘못됐습니다.")
            }
            result["walkRowsAndReducedMotion"] = true
            func activity(_ kind: String, state: String = "running", summary: String) {
                companion.receive(RunEvent(sessionId: session.id, type: "activity", activity: AgentActivity(id: "pet-task", provider: "claude", kind: kind, state: state, summary: summary)), snapshot: snapshot)
            }
            activity("read", summary: "README.md 문서 읽기")
            panel.contentView = NSHostingView(rootView: CompanionOverlayView(companion: companion).preferredColorScheme(.dark))
            panel.orderFrontRegardless()
            stage = "mounted"
            try await store.waitForSmoke(timeout: 3) {
                probe = findProbe(panel.contentView)
                return probe?.motion != nil && probe?.bounds.width ?? 0 > 0 && node(panel, id: "pet-task-bubble") != nil
            }
            guard let probe, let motion = probe.motion else { throw MightyError("실제 펫 포인터 영역을 찾지 못했습니다.") }
            try await store.waitForSmoke(timeout: 2) { probe.renderedRow == CompanionPetAnimation.reviewing.rawValue }
            result["readTaskRow"] = probe.renderedRow
            let original = panel.frame.origin
            let start = screenCenter(probe, panel: panel)
            let originalClicks = probe.clickCount
            result["initialProbe"] = probe.diagnostic
            result["initialScreenPoint"] = NSStringFromPoint(start)
            if let button = node(panel, id: "pet-toggle-bubble"), button.responds(to: NSSelectorFromString("accessibilityFrame")),
               let frame = (button.value(forKey: "accessibilityFrame") as? NSValue)?.rectValue { result["petButtonScreenFrame"] = NSStringFromRect(frame) }
            stage = "right-drag"
            try await post(.leftMouseDown, screen: start, panel: panel)
            try await post(.leftMouseDragged, screen: start.offset(x: 60, y: 12), panel: panel)
            try await store.waitForSmoke(timeout: 2) { motion.direction == .walkRight && probe.renderedRow == 1 && near(panel.frame.origin, original.offset(x: 60, y: 12)) }
            try require(near(NSPointFromString(probe.diagnostic["screenPoint"] ?? ""), start.offset(x: 60, y: 12)), "이벤트 큐가 드래그 화면 좌표를 변경했습니다.")
            result["firstDeliveredDrag"] = probe.diagnostic
            try require(motion.isDragging && probe.clickCount == originalClicks && node(panel, id: "pet-task-bubble") != nil, "드래그가 말풍선 클릭으로 처리되었습니다.")
            result["rightScreenshot"] = try store.captureSmokeWindow(panel, filename: "pet-walk-right.png").path
            result["rightOrigin"] = NSStringFromPoint(panel.frame.origin)
            stage = "left-drag"
            // Rebuild local coordinates after the panel moved. The gesture uses
            // stable screen points, not coordinates that move with the panel.
            try await post(.leftMouseDragged, screen: start.offset(x: -30, y: 12), panel: panel)
            try await store.waitForSmoke(timeout: 2) { motion.direction == .walkLeft && probe.renderedRow == 2 && near(panel.frame.origin, original.offset(x: -30, y: 12)) }
            result["leftScreenshot"] = try store.captureSmokeWindow(panel, filename: "pet-walk-left.png").path
            result["leftOrigin"] = NSStringFromPoint(panel.frame.origin)
            try await post(.leftMouseUp, screen: start.offset(x: -30, y: 170), panel: panel)
            try await store.waitForSmoke(timeout: 2) { !motion.isDragging && motion.direction == nil && probe.renderedRow == 8 }
            try require(probe.clickCount == originalClicks && node(panel, id: "pet-task-bubble") != nil, "펫을 놓으면서 말풍선이 바뀌었습니다.")
            try require(NSApp.keyWindow === previousKeyWindow, "펫 이동이 기존 입력 창의 포커스를 가져갔습니다.")
            result["screenCoordinateDragBothDirections"] = true
            result["outsideReleaseRestoresTaskWithoutClick"] = true
            result["dragPreservesKeyWindow"] = true

            stage = "click-versus-drag"
            let beforeTap = panel.frame.origin
            let tap = screenCenter(probe, panel: panel)
            try await post(.leftMouseDown, screen: tap, panel: panel)
            try await post(.leftMouseDragged, screen: tap.offset(x: 2, y: 1), panel: panel)
            try await post(.leftMouseUp, screen: tap.offset(x: 2, y: 1), panel: panel)
            try await store.waitForSmoke(timeout: 2) { probe.clickCount == originalClicks + 1 && node(panel, id: "pet-task-bubble") == nil }
            try require(panel.frame.origin == beforeTap, "클릭 허용 범위의 작은 이동이 패널을 움직였습니다.")
            try await post(.leftMouseDown, screen: screenCenter(probe, panel: panel), panel: panel)
            try await post(.leftMouseUp, screen: screenCenter(probe, panel: panel), panel: panel)
            try await store.waitForSmoke(timeout: 2) { probe.clickCount == originalClicks + 2 && node(panel, id: "pet-task-bubble") != nil }
            result["clickTogglesExactlyOnce"] = true

            stage = "pause-and-escape"
            let pause = screenCenter(probe, panel: panel)
            try await post(.leftMouseDown, screen: pause, panel: panel)
            try await post(.leftMouseDragged, screen: pause.offset(x: 20), panel: panel)
            try await store.waitForSmoke(timeout: 2) { motion.isDragging && motion.direction == .walkRight }
            try await store.waitForSmoke(timeout: 2) { motion.isDragging && motion.direction == nil && probe.renderedRow == 8 }
            result["pauseRestoresTaskButRetainsDragOwnership"] = true
            guard let escape = NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: [], timestamp: ProcessInfo.processInfo.systemUptime, windowNumber: panel.windowNumber, context: nil, characters: "\u{1b}", charactersIgnoringModifiers: "\u{1b}", isARepeat: false, keyCode: 53) else { throw MightyError("취소 키를 만들지 못했습니다.") }
            NSApp.postEvent(escape, atStart: false)
            try await store.waitForSmoke(timeout: 2) { !motion.isDragging && motion.direction == nil }
            try await post(.leftMouseUp, screen: screenCenter(probe, panel: panel), panel: panel)
            try await Task.sleep(for: .milliseconds(80))
            try require(probe.clickCount == originalClicks + 2 && node(panel, id: "pet-task-bubble") != nil, "취소된 드래그가 클릭으로 처리되었습니다.")
            result["escapeCancelsWithoutToggle"] = true

            stage = "live-task-and-lifecycle"
            activity("command", summary: "swift test 실행")
            try await store.waitForSmoke(timeout: 2) { probe.renderedRow == 7 && companion.current?.summary == "swift test 실행" }
            activity("tool", state: "waiting", summary: "권한 확인 대기")
            try await store.waitForSmoke(timeout: 2) { probe.renderedRow == 6 && companion.current?.status == "waiting" }
            companion.preferences.reducedMotion = true
            snapshot.sessions[0].status = "error"; companion.refresh(snapshot)
            try await store.waitForSmoke(timeout: 2) { probe.renderedRow == 5 }
            result["taskScreenshot"] = try store.captureSmokeWindow(panel, filename: "pet-task-failed.png").path
            snapshot.sessions[0].status = "completed"; companion.refresh(snapshot)
            try await store.waitForSmoke(timeout: 2) { probe.renderedRow == 4 }
            result["mountedTaskRows"] = ["read": 8, "command": 7, "waiting": 6, "error": 5, "completed": 4]
            let closing = screenCenter(probe, panel: panel)
            try await post(.leftMouseDown, screen: closing, panel: panel)
            try await post(.leftMouseDragged, screen: closing.offset(x: 12), panel: panel)
            try await store.waitForSmoke(timeout: 2) { motion.isDragging }
            panel.close()
            try await store.waitForSmoke(timeout: 2) { !motion.isDragging && motion.direction == nil }
            result["closeCancelsDrag"] = true
            result["clickCount"] = probe.clickCount
            result["dragCount"] = probe.dragCount
            result["passed"] = true
        } catch {
            result["stage"] = stage; result["error"] = error.localizedDescription
            result["panelOrigin"] = NSStringFromPoint(panel.frame.origin)
            result["row"] = probe?.renderedRow ?? -1
            result["dragging"] = probe?.motion?.isDragging ?? false
            result["clickCount"] = probe?.clickCount ?? -1
            result["nativeProbe"] = probe?.diagnostic ?? [:]
            result["currentProbe"] = findProbe(panel.contentView)?.diagnostic ?? [:]
            if let url = try? store.captureSmokeWindow(panel, filename: "pet-motion-failure.png") { result["failureScreenshot"] = url.path }
        }
        return result
    }

    private static func policyChecks() -> [String: Bool] {
        func agent(_ status: String = "running", kind: String? = nil, summary: String = "작업 중") -> AgentPresence {
            AgentPresence(id: "policy", title: "검증", workspace: "검증", provider: "claude", status: status, summary: summary, activity: kind.map { AgentActivity(provider: "claude", kind: $0, state: "running", summary: summary) })
        }
        return [
            "thinkingAndWriting": ["turn", "thinking", "edit", "write", "command", "agent"].allSatisfy { CompanionPetAnimationPolicy.task(agent(kind: $0, summary: "read word does not override the structured kind"), celebrating: false) == .working },
            "readingAndSearching": ["read", "search", "web", "review"].allSatisfy { CompanionPetAnimationPolicy.task(agent(kind: $0), celebrating: false) == .reviewing },
            "waiting": CompanionPetAnimationPolicy.task(agent("waiting"), celebrating: false) == .waiting,
            "failedAndStopped": ["error", "stopped", "cancelled"].allSatisfy { CompanionPetAnimationPolicy.task(agent($0), celebrating: true) == .failed },
            "completionReturnsToIdle": CompanionPetAnimationPolicy.task(agent("completed"), celebrating: true) == .jumping && CompanionPetAnimationPolicy.task(agent("completed"), celebrating: false) == .idle,
            "legacySummaryFallback": CompanionPetAnimationPolicy.task(agent(summary: "변경 내용 검토"), celebrating: false) == .reviewing,
            "emptyIdle": CompanionPetAnimationPolicy.task(nil, celebrating: false) == .idle,
        ]
    }
    private static func findProbe(_ view: NSView?) -> CompanionPetInteractionProbe? {
        guard let view else { return nil }
        if let found = view as? CompanionPetInteractionProbe { return found }
        return view.subviews.lazy.compactMap(findProbe).first
    }
    private static func node(_ object: Any, id: String, depth: Int = 0) -> NSObject? {
        guard depth < 35, let object = object as? NSObject else { return nil }
        if object.responds(to: NSSelectorFromString("accessibilityIdentifier")), object.value(forKey: "accessibilityIdentifier") as? String == id { return object }
        let children = object.responds(to: NSSelectorFromString("accessibilityChildren")) ? object.value(forKey: "accessibilityChildren") as? [Any] ?? [] : []
        return children.lazy.compactMap { node($0, id: id, depth: depth + 1) }.first
    }
    private static func screenCenter(_ probe: NSView, panel: NSWindow) -> NSPoint {
        let bounds = probe.bounds.intersection(probe.visibleRect)
        return panel.convertPoint(toScreen: probe.convert(NSPoint(x: bounds.midX, y: bounds.midY), to: nil))
    }
    private static func near(_ left: NSPoint, _ right: NSPoint) -> Bool { abs(left.x - right.x) < 1 && abs(left.y - right.y) < 1 }
    private static func post(_ type: NSEvent.EventType, screen: NSPoint, panel: NSWindow) async throws {
        // WindowServer learns a newly shown/moved nonactivating panel's frame
        // asynchronously. Validate its CG roundtrip before queuing real input.
        let deadline = Date().addingTimeInterval(1)
        repeat {
            let point = panel.convertPoint(fromScreen: screen)
            guard let mouse = NSEvent.mouseEvent(with: type, location: point, modifierFlags: [], timestamp: ProcessInfo.processInfo.systemUptime, windowNumber: panel.windowNumber, context: nil, eventNumber: 1, clickCount: 1, pressure: type == .leftMouseUp ? 0 : 1),
                  let cg = mouse.cgEvent else { throw MightyError("펫 포인터 이벤트를 만들지 못했습니다.") }
            cg.location = CGPoint(x: screen.x, y: (NSScreen.screens.first?.frame.maxY ?? 0) - screen.y)
            cg.setIntegerValueField(.mouseEventWindowUnderMousePointer, value: Int64(panel.windowNumber))
            cg.setIntegerValueField(.mouseEventWindowUnderMousePointerThatCanHandleThisEvent, value: Int64(panel.windowNumber))
            cg.setIntegerValueField(.eventTargetUnixProcessID, value: Int64(ProcessInfo.processInfo.processIdentifier))
            if let event = NSEvent(cgEvent: cg), event.window === panel, event.type == type, near(event.locationInWindow, point) {
                NSApp.postEvent(event, atStart: false)
                return
            }
            try await Task.sleep(for: .milliseconds(20))
        } while Date() < deadline
        throw MightyError("펫 패널의 네이티브 화면 좌표가 안정되지 않았습니다.")
    }
}

private extension NSPoint {
    func offset(x: CGFloat, y: CGFloat = 0) -> NSPoint { NSPoint(x: self.x + x, y: self.y + y) }
}
