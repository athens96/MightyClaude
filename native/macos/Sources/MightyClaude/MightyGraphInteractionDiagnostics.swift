import AppKit
import MightyCore
import SwiftUI

@MainActor
enum MightyGraphInteractionDiagnostics {
    /// Application-local events in an isolated fixture; no global input,
    /// clipboard access, provider request, or production session mutation.
    static func run(store: AppStore) async -> [String: Any] {
        var report: [String: Any] = ["passed": false, "aiRequestSent": false, "eventDelivery": "NSApp.postEvent"]
        guard ProcessInfo.processInfo.arguments.contains("--profile") else { return report }
        let previousWindow = NSApp.keyWindow
        let window = NSWindow(contentRect: NSRect(x: 120, y: 130, width: 940, height: 680), styleMask: [.titled, .closable], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        window.title = "그래프 카메라 검증"
        var probe: MightyGraphInteractionProbe?
        defer { window.orderOut(nil); window.close(); previousWindow?.makeKeyAndOrderFront(nil) }
        do {
            let output = (0..<45).map { "본문 \($0) · 그래프와 독립적으로 선택하고 스크롤하는 출력입니다." }.joined(separator: "\n\n")
            let entries = [LogEntry(id: "wheel-output", kind: "assistant", text: output, provider: "claude")]
            let run = MightyGraphRun(id: "wheel-run", input: (0..<16).map { "입력 \($0) · 긴 요청 미리보기" }.joined(separator: "\n"), status: "running", rootEntries: entries, agents: [
                MightyGraphAgent(id: "left", title: "실행 중", status: "running", entries: entries),
                MightyGraphAgent(id: "middle", title: "대기 중", status: "waiting", entries: entries),
                MightyGraphAgent(id: "right", title: "완료", status: "completed", entries: entries),
                MightyGraphAgent(id: "nested", parentID: "left", title: "중첩 작업", status: "running", entries: entries)
            ])
            let rootID = MightyGraphLayout.nodeID(run, suffix: "request")
            let host = NSHostingView(rootView: MightyGraphView(sessionID: "wheel-fixture", provider: "claude", runs: [run], draft: "", running: true, onFocus: {}))
            window.contentView = host
            NSApp.activate(ignoringOtherApps: true)
            window.makeKeyAndOrderFront(nil)
            try await store.waitForSmoke(timeout: 3) {
                probe = descendants(host, as: MightyGraphInteractionProbe.self).first
                return window.isKeyWindow && probe.map { $0.bounds.width > 0 && $0.bounds.height > 0 } == true && editor(in: host, nodeID: rootID) != nil
            }
            guard let probe, probe.enclosingScrollView == nil,
                  let editor = editor(in: host, nodeID: rootID), let inner = editor.enclosingScrollView else { throw MightyError("고정 카메라 뷰포트와 네이티브 출력창을 찾지 못했습니다.") }
            // Initial centering has its own graph fixture. Here a known camera
            // origin keeps both root and child hit targets fully in view.
            probe.setPanOffset(.zero)
            editor.cancelInitialScroll()
            scroll(inner, to: NSPoint(x: 0, y: 200))
            try await Task.sleep(for: .milliseconds(100))
            let initialPan = probe.panOffset
            let viewportFrame = probe.convert(probe.bounds, to: nil)
            let hostBounds = host.bounds
            report["initialGeometry"] = ["pan": NSStringFromPoint(initialPan), "viewport": NSStringFromRect(viewportFrame), "hostBounds": NSStringFromRect(hostBounds), "innerClip": NSStringFromRect(inner.contentView.bounds)]
            let innerBefore = inner.contentView.bounds.origin
            let forwardedBefore = probe.forwardedWheels
            report["firstWheel"] = try postWheel(window: window, at: center(inner.contentView), dy: -65)
            try await store.waitForSmoke(timeout: 3) { probe.forwardedWheels > forwardedBefore && changed(probe.panOffset, initialPan) }
            guard !changed(inner.contentView.bounds.origin, innerBefore), pinned(probe, host: host, frame: viewportFrame, hostBounds: hostBounds) else { throw MightyError("미선택 휠이 블록 내부나 고정 뷰포트를 움직였습니다.") }
            report["unselectedWheelMovesOnlyCamera"] = true
            report["fixedViewportWithoutOuterScrollView"] = true

            probe.setPanOffset(initialPan)
            try await Task.sleep(for: .milliseconds(80))
            try click(window, at: headerPoint(probe, id: rootID))
            try await store.waitForSmoke(timeout: 3) { probe.selectedNodeID == rootID }
            let selectedPan = probe.panOffset
            let cardFrame = inner.convert(inner.bounds, to: nil)
            let selectedInner = inner.contentView.bounds.origin
            let nativeBefore = probe.nativeWheels
            try postWheel(window: window, at: center(inner.contentView), dy: -65)
            try await store.waitForSmoke(timeout: 3) { probe.nativeWheels > nativeBefore && changed(inner.contentView.bounds.origin, selectedInner) }
            guard !changed(probe.panOffset, selectedPan), inner.convert(inner.bounds, to: nil) == cardFrame else { throw MightyError("선택 블록의 본문 휠이 카메라나 부모 영역을 움직였습니다.") }
            report["singleClickSelectsBlock"] = true
            report["selectedWheelMovesOnlyTranscript"] = true
            let headerInner = inner.contentView.bounds.origin
            let headerCount = probe.nativeWheels
            try postWheel(window: window, at: headerPoint(probe, id: rootID), dy: -35)
            try await store.waitForSmoke(timeout: 3) { probe.nativeWheels > headerCount && changed(inner.contentView.bounds.origin, headerInner) }
            guard !changed(probe.panOffset, selectedPan), inner.convert(inner.bounds, to: nil) == cardFrame else { throw MightyError("선택 블록의 헤더 휠이 카메라나 부모 영역을 움직였습니다.") }
            report["selectedHeaderScrollsContentOnly"] = true
            scroll(inner, to: NSPoint(x: 0, y: max(0, editor.bounds.height - inner.contentView.bounds.height)))
            let edgeCount = probe.nativeWheels
            try postWheel(window: window, at: center(inner.contentView), dy: -100)
            try await store.waitForSmoke(timeout: 3) { probe.nativeWheels > edgeCount }
            try await Task.sleep(for: .milliseconds(80))
            guard !changed(probe.panOffset, selectedPan), inner.convert(inner.bounds, to: nil) == cardFrame,
                  pinned(probe, host: host, frame: viewportFrame, hostBounds: hostBounds) else { throw MightyError("선택 블록의 끝에서 휠이 카메라나 부모로 전파됐습니다.") }
            report["selectedEdgeNeverPansCamera"] = true
            report["selectedParentGeometryPinned"] = true
            report["selectedScreenshot"] = try store.captureSmokeWindow(window, filename: "mighty-graph-selected-scroll.png").path

            // A selected card never captures another card's wheel. During a
            // gesture the active finger movement follows the current block.
            let childID = MightyGraphLayout.nodeID(run, suffix: "agent:left")
            guard let child = self.editor(in: host, nodeID: childID), let childScroll = child.enclosingScrollView else { throw MightyError("하위 블록의 출력창이 없습니다.") }
            child.cancelInitialScroll()
            scroll(inner, to: NSPoint(x: 0, y: 200))
            let phaseInner = inner.contentView.bounds.origin
            let phaseNative = probe.nativeWheels
            try postWheel(window: window, at: center(inner.contentView), dy: -25, phase: .began)
            try await store.waitForSmoke(timeout: 3) { probe.nativeWheels > phaseNative && changed(inner.contentView.bounds.origin, phaseInner) }
            let crossingPan = probe.panOffset
            let crossingInner = inner.contentView.bounds.origin
            let crossingChild = childScroll.contentView.bounds.origin
            let crossingCount = probe.forwardedWheels
            report["otherWheel"] = try postWheel(window: window, at: center(childScroll.contentView), dy: -25, phase: .changed)
            try await store.waitForSmoke(timeout: 3) { probe.forwardedWheels > crossingCount && changed(probe.panOffset, crossingPan) }
            guard !changed(inner.contentView.bounds.origin, crossingInner), !changed(childScroll.contentView.bounds.origin, crossingChild) else { throw MightyError("연속 휠이 비선택 블록의 내부 출력을 움직였습니다.") }
            let endingCount = probe.forwardedWheels
            try postWheel(window: window, at: center(childScroll.contentView), phase: .ended)
            try await store.waitForSmoke(timeout: 3) { probe.forwardedWheels > endingCount }
            report["otherBlockWheelMovesOnlyCamera"] = true
            report["phasedPointerCrossingMovesOnlyCamera"] = true
            report["nativeEventPhasesVerified"] = true
            try click(window, at: headerPoint(probe, id: childID))
            try await store.waitForSmoke(timeout: 3) { probe.selectedNodeID == childID }
            let childPan = probe.panOffset
            let childOrigin = childScroll.contentView.bounds.origin
            let childCount = probe.nativeWheels
            try postWheel(window: window, at: center(childScroll.contentView), dy: 25)
            try await store.waitForSmoke(timeout: 3) { probe.nativeWheels > childCount && changed(childScroll.contentView.bounds.origin, childOrigin) }
            guard !changed(probe.panOffset, childPan) else { throw MightyError("선택 하위 블록의 휠이 카메라를 움직였습니다.") }
            report["selectedChildWheelMovesOnlyTranscript"] = true

            probe.setPanOffset(initialPan)
            try await Task.sleep(for: .milliseconds(80))
            let background = try backgroundPoint(probe)
            let dragEnd = NSPoint(x: background.x + 55, y: background.y + 30)
            let beforeDrag = probe.panOffset
            try drag(window, from: probe.convert(background, to: nil), to: probe.convert(dragEnd, to: nil))
            try await store.waitForSmoke(timeout: 3) { changed(probe.panOffset, beforeDrag) && !probe.isPanning }
            guard abs(probe.panOffset.x - beforeDrag.x - 55) < 1, abs(probe.panOffset.y - beforeDrag.y - 30) < 1,
                  probe.selectedNodeID == nil, pinned(probe, host: host, frame: viewportFrame, hostBounds: hostBounds) else { throw MightyError("배경 드래그의 카메라 이동·선택 해제·고정 뷰포트가 올바르지 않습니다.") }
            report["backgroundDragPansCameraAndClearsSelection"] = true
            report["backgroundClickClearsSelection"] = true

            probe.setPanOffset(initialPan)
            try await Task.sleep(for: .milliseconds(80))
            scroll(inner, to: .zero)
            editor.setSelectedRange(NSRange(location: 0, length: 0))
            let source = editor.string as NSString
            let first = source.range(of: "본문 0")
            let second = source.range(of: "본문 1")
            guard first.location != NSNotFound, second.location != NSNotFound else { throw MightyError("네이티브 텍스트 선택 fixture가 없습니다.") }
            let textPan = probe.panOffset
            try drag(window, from: glyphPoint(editor, character: first.location), to: glyphPoint(editor, character: NSMaxRange(second)))
            try await store.waitForSmoke(timeout: 3) { editor.selectedRange().length > 0 }
            let selected = (editor.string as NSString).substring(with: editor.selectedRange())
            guard selected.contains("본문 0"), selected.contains("본문 1"), !changed(probe.panOffset, textPan), !probe.isPanning else { throw MightyError("본문 드래그가 문단 선택 대신 카메라를 움직였습니다.") }
            report["nativeTextDragSelectsWithoutPanning"] = true
            report["nativeSelectedRange"] = ["location": editor.selectedRange().location, "length": editor.selectedRange().length]
            try escape(window)
            try await store.waitForSmoke(timeout: 3) { probe.selectedNodeID == nil }
            report["escapeClearsSelection"] = true

            // Positive and negative offsets beyond the former document bounds
            // are reached through real wheel events, never through the setter.
            let freePoint = probe.convert(try backgroundPoint(probe), to: nil)
            let positiveCount = probe.forwardedWheels
            try postWheel(window: window, at: freePoint, dx: 1600, dy: 1600)
            try await store.waitForSmoke(timeout: 3) { probe.forwardedWheels > positiveCount && probe.panOffset.x > 500 && probe.panOffset.y > 500 }
            let positivePan = probe.panOffset
            let negativeCount = probe.forwardedWheels
            try postWheel(window: window, at: center(probe), dx: -4000, dy: -4000)
            try await store.waitForSmoke(timeout: 3) { probe.forwardedWheels > negativeCount && probe.panOffset.x < -1500 && probe.panOffset.y < -1500 }
            guard pinned(probe, host: host, frame: viewportFrame, hostBounds: hostBounds) else { throw MightyError("무경계 카메라 이동이 부모 뷰 크기를 늘렸습니다.") }
            report["unboundedBothAxesBeyondFormerBounds"] = true
            report["unboundedPanGeometry"] = ["positive": NSStringFromPoint(positivePan), "negative": NSStringFromPoint(probe.panOffset), "viewport": NSStringFromRect(probe.convert(probe.bounds, to: nil))]
            probe.setPanOffset(initialPan)
            try await store.waitForSmoke(timeout: 3) { self.editor(in: host, nodeID: rootID)?.window === window }
            report["cameraScreenshot"] = try store.captureSmokeWindow(window, filename: "mighty-graph-camera.png").path
            report["forwardedWheels"] = probe.forwardedWheels
            report["nativeWheels"] = probe.nativeWheels
            report["lastMonitorDiagnostic"] = probe.diagnostic

            let retiredCount = probe.forwardedWheels + probe.nativeWheels
            let plainHost = NSHostingView(rootView: AgentTranscriptView(sessionId: "wheel-basic", provider: "claude", running: false, entries: entries, onFocus: {}))
            window.contentView = plainHost
            try await store.waitForSmoke(timeout: 3) { probe.window == nil && descendants(plainHost, as: AgentTranscriptTextView.self).first?.enclosingScrollView != nil }
            guard let plain = descendants(plainHost, as: AgentTranscriptTextView.self).first, let plainScroll = plain.enclosingScrollView else { throw MightyError("기본 출력창 복귀에 실패했습니다.") }
            plain.cancelInitialScroll(); scroll(plainScroll, to: NSPoint(x: 0, y: 100))
            let plainBefore = plainScroll.contentView.bounds.origin
            try postWheel(window: window, at: center(plainScroll.contentView), dy: -65)
            try await store.waitForSmoke(timeout: 3) { changed(plainScroll.contentView.bounds.origin, plainBefore) }
            guard retiredCount == probe.forwardedWheels + probe.nativeWheels else { throw MightyError("제거된 카메라 모니터가 기본 출력창 휠을 처리했습니다.") }
            report["completedBasicTranscriptAndMonitorCleanup"] = true
            report["activity"] = try await activity(store: store, window: window)
            report["passed"] = true
        } catch {
            report["error"] = error.localizedDescription
            if let probe {
                report["failureSelection"] = probe.selectedNodeID ?? "none"
                report["failurePanOffset"] = NSStringFromPoint(probe.panOffset)
                report["failureForwardedWheels"] = probe.forwardedWheels
                report["failureNativeWheels"] = probe.nativeWheels
                report["failureMonitorDiagnostic"] = probe.diagnostic
            }
            if let url = try? store.captureSmokeWindow(window, filename: "mighty-graph-interaction-failure.png") { report["failureScreenshot"] = url.path }
        }
        return report
    }

    private static func activity(store: AppStore, window: NSWindow) async throws -> [String: Any] {
        let firstDate = Date(timeIntervalSinceReferenceDate: 0.1)
        let secondDate = Date(timeIntervalSinceReferenceDate: 0.6)
        guard ["running", "starting", "queued"].allSatisfy(MightyGraphActivityStyle.isActive),
              ["waiting", "completed", "error", "stopped"].allSatisfy({ !MightyGraphActivityStyle.isActive($0) }),
              MightyGraphActivityStyle.phase(at: firstDate, reducedMotion: true) == 0,
              MightyGraphActivityStyle.phase(at: secondDate, reducedMotion: true) == 0,
              MightyGraphActivityStyle.phase(at: firstDate, reducedMotion: false) != MightyGraphActivityStyle.phase(at: secondDate, reducedMotion: false) else { throw MightyError("진행·대기·완료·동작 줄이기 애니메이션 정책이 잘못됐습니다.") }
        let host = NSHostingView(rootView: ActivityFixture(status: "running"))
        window.contentView = host
        try await Task.sleep(for: .milliseconds(160))
        let bounds = host.bounds
        let first = try pixels(host)
        let firstURL = try store.captureSmokeWindow(window, filename: "mighty-graph-activity-first.png")
        try await Task.sleep(for: .milliseconds(280))
        let second = try pixels(host)
        let secondURL = try store.captureSmokeWindow(window, filename: "mighty-graph-activity-second.png")
        let reduced = NSWorkspace.shared.accessibilityDisplayShouldReduceMotion
        guard host.bounds == bounds, reduced ? first == second : first != second else { throw MightyError("실행 표시의 실제 두 프레임 또는 고정된 크기 검증에 실패했습니다.") }
        host.rootView = ActivityFixture(status: "completed")
        try await Task.sleep(for: .milliseconds(100))
        let completedFirst = try pixels(host)
        try await Task.sleep(for: .milliseconds(180))
        guard completedFirst == (try pixels(host)) else { throw MightyError("완료된 실행 표시가 계속 움직입니다.") }
        return ["passed": true, "activeFramesDiffer": first != second, "systemReduceMotion": reduced, "reducedMotionPolicyStatic": true, "completedFramesIdentical": true, "geometryUnchanged": true, "firstScreenshot": firstURL.path, "secondScreenshot": secondURL.path]
    }

    private struct ActivityFixture: View {
        let status: String
        var body: some View {
            ZStack {
                Color(nsColor: .windowBackgroundColor)
                MightyGraphActivityIndicator(status: status, tint: .orange)
                    .frame(width: 140, height: 52)
                    .overlay { MightyGraphActivityOutline(status: status, tint: .orange) }
            }
        }
    }
    private static func pixels(_ view: NSView) throws -> Data {
        guard let bitmap = view.bitmapImageRepForCachingDisplay(in: view.bounds) else { throw MightyError("애니메이션 비트맵을 만들지 못했습니다.") }
        view.cacheDisplay(in: view.bounds, to: bitmap)
        guard let bytes = bitmap.bitmapData else { throw MightyError("애니메이션 픽셀을 읽지 못했습니다.") }
        return Data(bytes: bytes, count: bitmap.bytesPerRow * bitmap.pixelsHigh)
    }
    private static func descendants<T: NSView>(_ view: NSView, as type: T.Type) -> [T] {
        (view as? T).map { [$0] } ?? view.subviews.flatMap { descendants($0, as: type) }
    }
    private static func editor(in view: NSView, nodeID: String) -> AgentTranscriptTextView? {
        descendants(view, as: AgentTranscriptTextView.self).first { $0.accessibilityIdentifier() == "transcript-graph-wheel-fixture-\(nodeID)" }
    }
    private static func scroll(_ view: NSScrollView, to point: NSPoint) {
        view.contentView.scroll(to: point); view.reflectScrolledClipView(view.contentView)
    }
    private static func changed(_ a: NSPoint, _ b: NSPoint) -> Bool { abs(a.x - b.x) > 1 || abs(a.y - b.y) > 1 }
    private static func center(_ view: NSView) -> NSPoint {
        let visible = view.bounds.intersection(view.visibleRect)
        return view.convert(NSPoint(x: visible.midX, y: visible.midY), to: nil)
    }
    private static func headerPoint(_ probe: MightyGraphInteractionProbe, id: String) throws -> NSPoint {
        guard let frame = probe.frames.first(where: { $0.0 == id })?.1 else { throw MightyError("선택할 그래프 카드가 없습니다.") }
        return probe.convert(NSPoint(x: frame.minX + 90, y: frame.minY + 18), to: nil)
    }
    private static func click(_ window: NSWindow, at point: NSPoint) throws {
        for type in [NSEvent.EventType.leftMouseDown, .leftMouseUp] {
            guard let event = NSEvent.mouseEvent(with: type, location: point, modifierFlags: [], timestamp: ProcessInfo.processInfo.systemUptime, windowNumber: window.windowNumber, context: nil, eventNumber: 1, clickCount: 1, pressure: type == .leftMouseDown ? 1 : 0) else { throw MightyError("블록 선택 이벤트를 만들지 못했습니다.") }
            NSApp.postEvent(event, atStart: false)
        }
    }
    @discardableResult
    private static func postWheel(window: NSWindow, at point: NSPoint, dx: Int32 = 0, dy: Int32 = 0, phase: NSEvent.Phase = []) throws -> [String: Any] {
        // A CG-created wheel loses its NSWindow on conversion. Starting with a
        // public window-targeted mouse event retains AppKit's window context.
        guard let mouse = NSEvent.mouseEvent(with: .mouseMoved, location: point, modifierFlags: [], timestamp: ProcessInfo.processInfo.systemUptime, windowNumber: window.windowNumber, context: nil, eventNumber: 1, clickCount: 0, pressure: 0),
              let cg = mouse.cgEvent else { throw MightyError("휠 이벤트를 만들지 못했습니다.") }
        cg.type = .scrollWheel
        let screen = window.convertPoint(toScreen: point)
        cg.location = CGPoint(x: screen.x, y: (NSScreen.screens.first?.frame.maxY ?? 0) - screen.y)
        cg.setIntegerValueField(.mouseEventWindowUnderMousePointer, value: Int64(window.windowNumber))
        cg.setIntegerValueField(.mouseEventWindowUnderMousePointerThatCanHandleThisEvent, value: Int64(window.windowNumber))
        cg.setIntegerValueField(.eventTargetUnixProcessID, value: Int64(ProcessInfo.processInfo.processIdentifier))
        cg.setIntegerValueField(.scrollWheelEventIsContinuous, value: 1)
        cg.setIntegerValueField(.scrollWheelEventPointDeltaAxis1, value: Int64(dy))
        cg.setIntegerValueField(.scrollWheelEventPointDeltaAxis2, value: Int64(dx))
        cg.setDoubleValueField(.scrollWheelEventFixedPtDeltaAxis1, value: Double(dy))
        cg.setDoubleValueField(.scrollWheelEventFixedPtDeltaAxis2, value: Double(dx))
        let rawPhase: Int64
        switch phase {
        case []: rawPhase = 0
        case .began: rawPhase = 1
        case .changed: rawPhase = 2
        case .ended: rawPhase = 4
        default: throw MightyError("지원하지 않는 검증 휠 단계입니다.")
        }
        cg.setIntegerValueField(.scrollWheelEventScrollPhase, value: rawPhase)
        cg.setIntegerValueField(.scrollWheelEventMomentumPhase, value: 0)
        guard let event = NSEvent(cgEvent: cg), event.window === window, event.type == .scrollWheel,
              event.phase == phase, event.momentumPhase.isEmpty,
              abs(event.locationInWindow.x - point.x) < 1, abs(event.locationInWindow.y - point.y) < 1,
              abs(event.scrollingDeltaX - CGFloat(dx)) < 0.1, abs(event.scrollingDeltaY - CGFloat(dy)) < 0.1 else { throw MightyError("합성 휠의 창·이동량·단계가 요청한 값과 다릅니다.") }
        let hit = window.contentView.flatMap { content in
            content.hitTest(content.superview?.convert(event.locationInWindow, from: nil) ?? event.locationInWindow)
        }
        var ancestors: [String] = []
        var current = hit
        while let view = current, ancestors.count < 24 { ancestors.append(String(describing: type(of: view))); current = view.superview }
        let hitScroll = hit?.enclosingScrollView
        let evidence: [String: Any] = ["requestedWindowPoint": NSStringFromPoint(point), "eventWindowPoint": NSStringFromPoint(event.locationInWindow), "hitAncestors": ancestors, "hitScroll": hitScroll.map { String(describing: type(of: $0)) } ?? "none", "hitScrollClip": NSStringFromRect(hitScroll?.contentView.bounds ?? .zero), "deltaX": event.scrollingDeltaX, "deltaY": event.scrollingDeltaY, "phase": event.phase.rawValue]
        NSApp.postEvent(event, atStart: false)
        return evidence
    }
    private static func pinned(_ probe: MightyGraphInteractionProbe, host: NSView, frame: NSRect, hostBounds: NSRect) -> Bool {
        probe.enclosingScrollView == nil && probe.convert(probe.bounds, to: nil) == frame && host.bounds == hostBounds
    }
    private static func backgroundPoint(_ probe: MightyGraphInteractionProbe) throws -> NSPoint {
        let candidates = [NSPoint(x: 8, y: 8), NSPoint(x: 8, y: probe.bounds.height / 2), NSPoint(x: probe.bounds.width - 80, y: 8)]
        guard let point = candidates.first(where: { point in
            let path = NSRect(x: point.x, y: point.y, width: 56, height: 31)
            return probe.bounds.contains(path) && !probe.frames.contains(where: { $0.1.intersects(path) })
        }) else { throw MightyError("배경 드래그에 필요한 빈 뷰포트를 찾지 못했습니다.") }
        return point
    }
    private static func drag(_ window: NSWindow, from start: NSPoint, to end: NSPoint) throws {
        for (index, item) in [(NSEvent.EventType.leftMouseDown, start), (.leftMouseDragged, end), (.leftMouseUp, end)].enumerated() {
            guard let event = NSEvent.mouseEvent(with: item.0, location: item.1, modifierFlags: [], timestamp: ProcessInfo.processInfo.systemUptime, windowNumber: window.windowNumber, context: nil, eventNumber: index + 1, clickCount: 1, pressure: item.0 == .leftMouseUp ? 0 : 1) else { throw MightyError("드래그 검증 이벤트를 만들지 못했습니다.") }
            NSApp.postEvent(event, atStart: false)
        }
    }
    private static func glyphPoint(_ editor: NSTextView, character: Int) throws -> NSPoint {
        guard let manager = editor.layoutManager, let container = editor.textContainer else { throw MightyError("텍스트 선택 위치를 찾지 못했습니다.") }
        manager.ensureLayout(for: container)
        let glyph = manager.glyphIndexForCharacter(at: character)
        let rect = manager.boundingRect(forGlyphRange: NSRange(location: glyph, length: 1), in: container)
        return editor.convert(NSPoint(x: rect.minX + editor.textContainerOrigin.x + 0.25, y: rect.midY + editor.textContainerOrigin.y), to: nil)
    }
    private static func escape(_ window: NSWindow) throws {
        guard let event = NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: [], timestamp: ProcessInfo.processInfo.systemUptime, windowNumber: window.windowNumber, context: nil, characters: "\u{1b}", charactersIgnoringModifiers: "\u{1b}", isARepeat: false, keyCode: 53) else { throw MightyError("Escape 이벤트를 만들지 못했습니다.") }
        NSApp.postEvent(event, atStart: false)
    }
}
