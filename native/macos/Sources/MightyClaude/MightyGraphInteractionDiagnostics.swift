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
            var savedSizes: [String: MightyGraphBlockSize] = [:]
            let host = NSHostingView(rootView: MightyGraphView(sessionID: "wheel-fixture", provider: "claude", runs: [run], draft: "", running: true,
                onSaveBlockSize: { id, size in savedSizes[id] = size }, onFocus: {}))
            window.contentView = host
            NSApp.activate(ignoringOtherApps: true)
            window.makeKeyAndOrderFront(nil)
            try await store.waitForSmoke(timeout: 3) {
                probe = descendants(host, as: MightyGraphInteractionProbe.self).first
                return window.isKeyWindow && probe.map(ready) == true && editor(in: host, nodeID: rootID) != nil
            }
            guard let probe, probe.enclosingScrollView == nil,
                  let editor = editor(in: host, nodeID: rootID), let inner = editor.enclosingScrollView else { throw MightyError("고정 카메라 뷰포트와 네이티브 출력창을 찾지 못했습니다.") }
            // Initial centering has its own graph fixture. Here a known camera
            // origin keeps both root and child hit targets fully in view — and
            // the setter only lands once the probe has published it back.
            probe.setPanOffset(.zero)
            try await store.waitForSmoke(timeout: 3) { probe.panOffset == .zero }
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

            // The selection outlives its focus here: a pan, a block resize or a
            // click on card chrome all hand first responder to the canvas, and
            // AppKit gives ⌘C to the first responder alone. Copy into a private
            // pasteboard so the check never touches the user's clipboard, and
            // press the key the way a Korean input source delivers it ("ㅊ").
            // The scope ends with the block, so the editor is handed its own
            // pasteboard back before anything else in this check runs.
            do {
                let board = NSPasteboard(name: NSPasteboard.Name("dev.mightyclaude.diagnostics.copy"))
                board.clearContents()
                editor.copyPasteboard = board
                defer { editor.copyPasteboard = .general; board.releaseGlobally() }
                guard window.makeFirstResponder(probe), window.firstResponder === probe else { throw MightyError("⌘C 검증용 포커스 이동에 실패했습니다.") }
                guard commandC(window) else { throw MightyError("포커스를 잃은 블록이 ⌘C 키 동등키를 받지 못했습니다.") }
                let copied = board.string(forType: .string)
                report["copiedAfterFocusLeftTheCard"] = copied ?? "none"
                // Attachment placeholders are layout, and the copy path drops
                // them, so the expectation is the selection without them too.
                let expected = selected.replacingOccurrences(of: "\u{FFFC}", with: "")
                guard copied == expected else { throw MightyError("포커스를 잃은 블록에서 ⌘C가 선택한 본문을 복사하지 못했습니다.") }
                report["copyFollowsSelectionAfterFocusLeavesTheCard"] = true
            }
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

            guard let realFrame = probe.frames.first(where: { $0.0 == rootID })?.1,
                  let realSize = probe.layoutFrames.first(where: { $0.0 == rootID })?.1.size,
                  let childBeforeResize = probe.layoutFrames.first(where: { $0.0 == childID })?.1,
                  let resizeEditor = self.editor(in: host, nodeID: rootID) else { throw MightyError("실제 그래프 크기 조절 대상을 찾지 못했습니다.") }
            // The unbounded camera checks deliberately unmount offscreen
            // transcripts. Compare identity across this resize only, after the
            // card has returned to the viewport and remounted.
            report["resizeEditorRemountedAfterUnboundedPan"] = resizeEditor !== editor
            let realStart = CGPoint(x: realFrame.maxX - 3, y: realFrame.maxY - 3)
            try drag(window, from: probe.convert(realStart, to: nil), to: probe.convert(CGPoint(x: realStart.x + 80, y: realStart.y + 70), to: nil))
            try await store.waitForSmoke(timeout: 3) {
                savedSizes[rootID]?.width == realSize.width + 80 && savedSizes[rootID]?.height == realSize.height + 70 && !probe.isResizing
                    && probe.layoutFrames.first(where: { $0.0 == rootID })?.1.size == CGSize(width: realSize.width + 80, height: realSize.height + 70)
            }
            try await Task.sleep(for: .milliseconds(120))
            let postResizeFrame = probe.frames.first(where: { $0.0 == rootID })?.1
            let postResizeChild = probe.layoutFrames.first(where: { $0.0 == childID })?.1
            report["actualGraphResizeEvidence"] = [
                "beforeFrame": NSStringFromRect(realFrame), "afterFrame": NSStringFromRect(postResizeFrame ?? .zero),
                "childBefore": NSStringFromRect(childBeforeResize), "childAfter": NSStringFromRect(postResizeChild ?? .zero),
                "panOffset": NSStringFromPoint(probe.panOffset),
                "sameEditorAcrossResize": self.editor(in: host, nodeID: rootID) === resizeEditor,
                "originalEditorStillMounted": editor.window === window
            ]
            guard let realAfter = probe.frames.first(where: { $0.0 == rootID })?.1,
                  let childAfterResize = probe.layoutFrames.first(where: { $0.0 == childID })?.1,
                  !changed(realAfter.origin, realFrame.origin), childAfterResize.minY >= childBeforeResize.minY + 69,
                  self.editor(in: host, nodeID: rootID) === resizeEditor else { throw MightyError("실제 그래프 크기 조절이 시작 모서리·하위 배치·출력창을 유지하지 못했습니다.") }
            report["actualGraphResizePreservesAnchorAndEditor"] = true
            report["actualGraphResizeReflowsChildren"] = true
            report["actualGraphResizeScreenshot"] = try store.captureSmokeWindow(window, filename: "mighty-graph-resized.png").path

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
            report["resize"] = try await resizing(store: store, window: window)
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

    private final class FlippedFixtureView: NSView { override var isFlipped: Bool { true } }

    /// A probe is ready once it has a real viewport, agrees with the size
    /// SwiftUI measured for it, and has admitted the camera target it was
    /// given: only then does a plain `setPanOffset` mean what it says.
    private static func ready(_ probe: MightyGraphInteractionProbe) -> Bool {
        probe.bounds.width > 0 && probe.bounds.height > 0
            && abs(probe.bounds.width - probe.expectedViewportSize.width) < 1
            && abs(probe.bounds.height - probe.expectedViewportSize.height) < 1
            && probe.targetToken == probe.consumedTargetToken
    }

    private static func resizing(store: AppStore, window: NSWindow) async throws -> [String: Any] {
        var results: [[String: Any]] = []
        for zoom: CGFloat in [0.5, 1, 1.5] {
            let container = FlippedFixtureView(frame: NSRect(x: 0, y: 0, width: 940, height: 680))
            let root = FlippedFixtureView(frame: container.bounds)
            container.addSubview(root)
            let inner = NSScrollView()
            inner.hasVerticalScroller = true
            let text = NSTextView(frame: NSRect(x: 0, y: 0, width: 280, height: 3200))
            text.isEditable = false
            text.string = (0..<160).map { "출력 \($0) · 크기를 바꾸어도 블록 스크롤을 유지합니다." }.joined(separator: "\n")
            inner.documentView = text
            root.addSubview(inner)
            let probe = MightyGraphInteractionProbe(frame: container.bounds)
            probe.sessionID = "resize-fixture"
            // The camera is only admitted for the viewport SwiftUI measured; a
            // hand-built probe states the same size it was given.
            probe.expectedViewportSize = container.bounds.size
            probe.graphRoot = root
            container.addSubview(probe)
            let nodeID = "resize-node"
            let initialSize = CGSize(width: 360, height: 240)
            var currentSize = initialSize
            var currentPan = CGPoint.zero
            var calls: [(CGSize, Bool)] = []
            func render() {
                // Deliberate tree reflow: changing a child can shift its own
                // computed origin. The drag must keep its screen anchor fixed.
                let origin = CGPoint(x: 120 + (currentSize.width - 360) * 0.3,
                                     y: 120 + (currentSize.height - 240) * 0.15)
                probe.updateLayoutFrames([(nodeID, CGRect(origin: origin, size: currentSize))], zoom: zoom, panOffset: currentPan)
                if let frame = probe.frames.first?.1 {
                    inner.frame = frame.insetBy(dx: 8 * zoom, dy: 24 * zoom)
                }
            }
            probe.onPan = { point in currentPan = point; if let frame = probe.frames.first?.1 { inner.frame = frame.insetBy(dx: 8 * zoom, dy: 24 * zoom) } }
            probe.onResize = { _, size, finished in calls.append((size, finished)); currentSize = size; render() }
            render()
            window.contentView = container
            window.makeKeyAndOrderFront(nil)
            probe.installMonitorIfNeeded()
            defer { probe.dispose() }
            try await Task.sleep(for: .milliseconds(80))
            func require(_ condition: Bool, _ message: String) throws {
                if !condition { throw MightyError("크기 조절 \(zoom)x: \(message)") }
            }
            func post(_ type: NSEvent.EventType, at point: CGPoint) throws {
                guard let event = NSEvent.mouseEvent(with: type, location: probe.convert(point, to: nil), modifierFlags: [], timestamp: ProcessInfo.processInfo.systemUptime,
                                                    windowNumber: window.windowNumber, context: nil, eventNumber: 1, clickCount: 1,
                                                    pressure: type == .leftMouseUp ? 0 : 1) else { throw MightyError("크기 조절 이벤트를 만들지 못했습니다.") }
                NSApp.postEvent(event, atStart: false)
            }
            func startDrag() async throws -> CGPoint {
                guard let frame = probe.frames.first?.1 else { throw MightyError("크기 조절 블록이 없습니다.") }
                let handle = probe.resizeHandleRect(for: frame)
                try require(handle.width >= 16 && abs(handle.width - max(16, 22 * zoom)) < 0.1, "모서리 클릭 영역")
                let point = CGPoint(x: frame.maxX - 2, y: frame.maxY - 2)
                try post(.leftMouseDown, at: point)
                try await store.waitForSmoke(timeout: 3) { probe.isResizing }
                return point
            }
            let anchor = probe.frames[0].1.origin
            let start = try await startDrag()
            try require(probe.selectedNodeID == nodeID && !probe.isPanning, "모서리 선택이 카메라 이동을 시작했습니다.")
            let forwardCount = probe.forwardedWheels
            let nativeCount = probe.nativeWheels
            let resizePan = probe.panOffset
            try postWheel(window: window, at: probe.convert(start, to: nil), dy: -30)
            try await Task.sleep(for: .milliseconds(60))
            try require(probe.forwardedWheels == forwardCount && probe.nativeWheels == nativeCount && probe.panOffset == resizePan, "크기 조절 중 휠 전달")

            let end = CGPoint(x: start.x + 90, y: start.y + 60)
            try post(.leftMouseDragged, at: end)
            try await store.waitForSmoke(timeout: 3) { !calls.isEmpty }
            let expected = CGSize(width: 360 + 90 / zoom, height: 240 + 60 / zoom)
            try require(currentSize == expected, "화면 이동량이 확대 배율로 변환되지 않았습니다.")
            try require(!changed(probe.frames[0].1.origin, anchor), "레이아웃 재배치로 시작 모서리가 이동했습니다.")
            try post(.leftMouseDragged, at: end)
            try await Task.sleep(for: .milliseconds(40))
            try require(currentSize == expected, "동일 마우스 위치에서 크기가 누적되었습니다.")
            try post(.leftMouseUp, at: end)
            try await store.waitForSmoke(timeout: 3) { !probe.isResizing && calls.contains(where: \.1) }
            try require(calls.filter(\.1).count == 1, "완료 콜백이 한 번이 아닙니다.")
            try require(!changed(probe.frames[0].1.origin, anchor), "완료 시 모서리 위치 변경")

            let beforeCancel = currentSize
            let cancelStart = try await startDrag()
            try post(.leftMouseDragged, at: CGPoint(x: cancelStart.x + 50, y: cancelStart.y + 40))
            try await store.waitForSmoke(timeout: 3) { currentSize != beforeCancel }
            try escape(window)
            try await store.waitForSmoke(timeout: 3) { !probe.isResizing && currentSize == beforeCancel }

            // Mouse-up outside the viewport still ends the same drag and clamps
            // graph dimensions, independent of zoom.
            let largeStart = try await startDrag()
            let outside = CGPoint(x: largeStart.x + 4000, y: largeStart.y + 4000)
            try post(.leftMouseDragged, at: outside)
            try post(.leftMouseUp, at: outside)
            try await store.waitForSmoke(timeout: 3) { !probe.isResizing && currentSize == CGSize(width: 1400, height: 1200) }
            currentSize = initialSize; currentPan = .zero; render()
            let smallStart = try await startDrag()
            let smallEnd = CGPoint(x: smallStart.x - 4000, y: smallStart.y - 4000)
            try post(.leftMouseDragged, at: smallEnd)
            try post(.leftMouseUp, at: smallEnd)
            try await store.waitForSmoke(timeout: 3) { !probe.isResizing && currentSize == CGSize(width: 300, height: 140) }

            currentSize = initialSize; currentPan = .zero; render()
            scroll(inner, to: NSPoint(x: 0, y: 200))
            let innerOrigin = inner.contentView.bounds.origin
            let beforeInner = probe.nativeWheels
            let innerPan = probe.panOffset
            try postWheel(window: window, at: center(inner.contentView), dy: -25)
            try await store.waitForSmoke(timeout: 3) { probe.nativeWheels > beforeInner && changed(inner.contentView.bounds.origin, innerOrigin) }
            try require(probe.panOffset == innerPan, "크기 조절 후 선택 블록 스크롤이 카메라 이동")
            let beforePan = probe.panOffset
            try drag(window, from: probe.convert(CGPoint(x: 8, y: 8), to: nil), to: probe.convert(CGPoint(x: 48, y: 38), to: nil))
            try await store.waitForSmoke(timeout: 3) { !probe.isPanning && changed(probe.panOffset, beforePan) }
            try require(abs(probe.panOffset.x - beforePan.x - 40) < 1 && abs(probe.panOffset.y - beforePan.y - 30) < 1, "크기 조절 후 배경 드래그")

            currentSize = initialSize; currentPan = .zero; render()
            let resignStart = try await startDrag()
            try post(.leftMouseDragged, at: CGPoint(x: resignStart.x + 35, y: resignStart.y + 25))
            try await store.waitForSmoke(timeout: 3) { currentSize != initialSize }
            NotificationCenter.default.post(name: NSWindow.didResignKeyNotification, object: window)
            try require(!probe.isResizing && currentSize == initialSize, "창 비활성화 취소")
            let disposeStart = try await startDrag()
            try post(.leftMouseDragged, at: CGPoint(x: disposeStart.x + 35, y: disposeStart.y + 25))
            try await store.waitForSmoke(timeout: 3) { currentSize != initialSize }
            probe.dispose()
            try require(!probe.isResizing && currentSize == initialSize, "뷰 제거 취소")
            results.append(["zoom": zoom, "screenDelta": "90,60", "graphSize": NSStringFromSize(expected),
                            "anchorPinned": true, "fixedOriginDelta": true, "wheelSuppressedDuringResize": true,
                            "outsideMouseUpAndClamps": true, "escapeRestoresSize": true,
                            "selectedInnerScrollPreserved": true, "backgroundPanPreserved": true,
                            "resignAndDisposeCancel": true])
        }
        return ["passed": true, "zooms": results]
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
    /// A Korean input source reports "ㅊ" for the C key, so the key equivalent
    /// carries exactly what AppKit delivers on that layout: only the key code
    /// still says "the user pressed ⌘C".
    private static func commandC(_ window: NSWindow) -> Bool {
        guard let event = NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: .command, timestamp: ProcessInfo.processInfo.systemUptime,
                                           windowNumber: window.windowNumber, context: nil, characters: "ㅊ", charactersIgnoringModifiers: "ㅊ",
                                           isARepeat: false, keyCode: TranscriptCopyClaim.copyKeyCode) else { return false }
        return window.performKeyEquivalent(with: event)
    }
    private static func escape(_ window: NSWindow) throws {
        guard let event = NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: [], timestamp: ProcessInfo.processInfo.systemUptime, windowNumber: window.windowNumber, context: nil, characters: "\u{1b}", charactersIgnoringModifiers: "\u{1b}", isARepeat: false, keyCode: 53) else { throw MightyError("Escape 이벤트를 만들지 못했습니다.") }
        NSApp.postEvent(event, atStart: false)
    }
}
