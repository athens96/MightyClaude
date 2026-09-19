import AppKit
import MightyCore
import SwiftUI

@MainActor
enum MightyGraphDiagnostics {
    /// Mounted graph fixtures only; never invokes a provider or the clipboard.
    static func run(store: AppStore) async -> [String: Any] {
        var report: [String: Any] = ["passed": false, "aiRequestSent": false]
        guard ProcessInfo.processInfo.arguments.contains("--profile") else { return report }
        let previousWindow = NSApp.keyWindow
        let previousSnapshot = store.snapshot
        let previousDrafts = store.drafts
        let window = NSWindow(contentRect: NSRect(x: 90, y: 120, width: 1020, height: 760), styleMask: [.titled, .closable, .resizable], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        window.title = "마이티 그래프 검증"
        defer {
            window.orderOut(nil); window.close()
            store.snapshot = previousSnapshot; store.drafts = previousDrafts
            previousWindow?.makeKeyAndOrderFront(nil)
        }
        do {
            let answer = LogEntry(id: "graph-answer", kind: "assistant", text: "## 최종 결과\n\n**검증 완료**: 요청과 하위 작업을 함께 표시합니다.\n\n```swift\nlet result = 42\n```", provider: "claude")
            let tool = LogEntry(id: "graph-tool", kind: "system", text: "swift test", activity: AgentActivity(id: "graph-tool", provider: "claude", kind: "command", state: "completed", summary: "swift test", output: "All tests passed"))
            let first = MightyGraphRun(id: "graph-first", input: "변경을 분석하고 검증해주세요.", status: "completed", rootEntries: [tool, answer], agents: [
                MightyGraphAgent(id: "research", title: "코드 분석", input: "관련 코드를 살펴보세요.", status: "completed", entries: [answer]),
                MightyGraphAgent(id: "verify", title: "테스트 검증", input: "독립적으로 검증하세요.", status: "completed", entries: [tool]),
                MightyGraphAgent(id: "lint", title: "스타일 검토", input: "규칙을 확인하세요.", status: "completed", entries: [tool]),
                MightyGraphAgent(id: "nested", parentID: "research", title: "하위 조사", input: "호출 경로를 확인하세요.", status: "completed", entries: [answer])
            ], resultEntries: [answer])
            let second = MightyGraphRun(id: "graph-second", input: "다음 변경도 이어서 처리해주세요.", status: "running", rootEntries: [], agents: [MightyGraphAgent(id: "waiting", title: "진행 중인 검토", input: "추가 결과를 기다립니다.", status: "waiting")])
            let fixture = GraphFixture(runs: [first, second])
            let layout = MightyGraphLayout.make(runs: fixture.runs, draft: "", running: true, expanded: [])
            try verifyGeometry(layout)
            guard layout.nodes.count == 8, !layout.nodes.contains(where: { $0.content == .draft }),
                  !layout.nodes.contains(where: { $0.content == .result(1) }),
                  layout.nodes.contains(where: { $0.content == .result(0) }) else { throw MightyError("진행 중인 요청에 최종 결과나 빈 다음 입력이 표시됩니다.") }
            let research = MightyGraphLayout.nodeID(first, suffix: "agent:research")
            let verify = MightyGraphLayout.nodeID(first, suffix: "agent:verify")
            let nested = MightyGraphLayout.nodeID(first, suffix: "agent:nested")
            guard let left = layout.nodes.first(where: { $0.id == research }), let right = layout.nodes.first(where: { $0.id == verify }),
                  left.frame.minY == right.frame.minY, left.frame.maxX < right.frame.minX,
                  layout.edges.contains(where: { $0.source == research && $0.target == nested }) else { throw MightyError("형제·중첩 에이전트 연결 구조가 올바르지 않습니다.") }
            report["horizontalSiblingsAndNestedEdges"] = true
            report["noEarlyResultOrEmptyPending"] = true
            var parentEnded = second
            parentEnded.status = "completed"
            guard !MightyGraphLayout.finished(parentEnded) else { throw MightyError("하위 에이전트가 대기 중인데 전체 작업을 완료로 표시합니다.") }
            let expanded = MightyGraphLayout.make(runs: fixture.runs, draft: "", running: true, expanded: [research])
            try verifyGeometry(expanded)
            guard expanded.size.height > layout.size.height else { throw MightyError("카드 확장이 뒤쪽 내용을 밀지 않았습니다.") }
            report["expandedCardsNeverOverlap"] = true

            let resizedIDs = [MightyGraphLayout.nodeID(first, suffix: "request"), research, verify, nested,
                              MightyGraphLayout.nodeID(first, suffix: "result"), "pending-input"]
            let sizes = Dictionary(uniqueKeysWithValues: resizedIDs.enumerated().map { index, id in
                (id, MightyGraphBlockSize(width: index.isMultiple(of: 2) ? 1100 : 320,
                                          height: index.isMultiple(of: 2) ? 700 : 160))
            })
            let resized = MightyGraphLayout.make(runs: fixture.runs, draft: "다음 요청", running: true, expanded: [], blockSizes: sizes)
            try verifyGeometry(resized)
            for node in resized.nodes {
                if let size = sizes[node.id] {
                    guard node.frame.size == CGSize(width: size.width, height: size.height) else { throw MightyError("블록별 크기가 레이아웃에 적용되지 않았습니다.") }
                }
            }
            report["customSizesReflowEveryBlockAndConnector"] = true

            window.contentView = NSHostingView(rootView: GraphFixtureView(fixture: fixture))
            window.makeKeyAndOrderFront(nil)
            let mainID = MightyGraphLayout.nodeID(first, suffix: "request")
            try await store.waitForSmoke(timeout: 3) { window.contentView.flatMap { camera(in: $0) } != nil }
            guard let content = window.contentView else { throw MightyError("그래프 캔버스가 없습니다.") }
            let latestID = MightyGraphLayout.nodeID(second, suffix: "request")
            try await store.waitForSmoke(timeout: 3) { (camera(in: content)?.viewportSize.width ?? 0) > 0 }
            guard let canvas = camera(in: content) else { throw MightyError("고정 다이어그램 뷰포트를 찾지 못했습니다.") }
            guard canvas.enclosingScrollView == nil else { throw MightyError("다이어그램이 여전히 바깥 문서 스크롤에 포함되어 있습니다.") }
            report["fixedViewportWithoutOuterDocumentScroll"] = true
            try await store.waitForSmoke(timeout: 3) { node(window, identifier: "mighty-graph-canvas-graph-fixture") != nil }
            report["nativeCanvasAccessibilityExposed"] = true
            try await store.waitForSmoke(timeout: 3) {
                guard let request = frame(node(window, identifier: "mighty-request-\(latestID)")) else { return false }
                let visible = window.convertToScreen(canvas.convert(canvas.bounds, to: nil))
                report["initialCameraGeometry"] = ["requestScreenFrame": NSStringFromRect(request), "viewportScreenFrame": NSStringFromRect(visible), "cameraOffset": NSStringFromPoint(canvas.panOffset)]
                return visible.insetBy(dx: -2, dy: -2).contains(request)
            }
            report["latestRequestInitiallyVisible"] = true
            report["latestScreenshot"] = try store.captureSmokeWindow(window, filename: "mighty-graph-latest.png").path
            // Move the camera to earlier history, then stream a log. A content
            // update must not recenter the fixed viewport.
            canvas.setPanOffset(.zero)
            try await store.waitForSmoke(timeout: 3) { canvas.panOffset == .zero }
            let manualOrigin = canvas.panOffset
            fixture.runs[1].rootEntries.append(LogEntry(id: "live-progress", kind: "assistant", text: "진행 상황이 갱신되었습니다.", provider: "claude"))
            try await Task.sleep(for: .milliseconds(100))
            guard abs(canvas.panOffset.y - manualOrigin.y) <= 2 && abs(canvas.panOffset.x - manualOrigin.x) <= 2 else { throw MightyError("출력 갱신이 사용자의 그래프 스크롤을 바꿨습니다.") }
            report["streamingPreservesManualScroll"] = true
            try await store.waitForSmoke(timeout: 3) {
                textViews(in: content).contains(where: { $0.string.contains("검증 완료") && $0.string.contains("let result = 42") })
            }
            let editors = textViews(in: content)
            guard editors.contains(where: { $0.string.contains("검증 완료") && $0.string.contains("let result = 42") }),
                  editors.contains(where: { $0.string.contains("swift test") }),
                  editors.allSatisfy({ !$0.isEditable && $0.isSelectable }) else { throw MightyError("그래프의 마크다운·도구 출력이 기존 선택 가능한 렌더러와 다릅니다.") }
            guard !editors.compactMap({ $0 as? AgentTranscriptTextView }).contains(where: { $0.string.contains(first.input) }) else { throw MightyError("요청이 고정 입력 영역과 출력 기록에 중복 표시됩니다.") }
            report["requestNotDuplicatedInOutput"] = true
            report["nativeMarkdownAndToolRenderer"] = true
            report["nativeTranscriptCount"] = editors.count
            report["screenshot"] = try store.captureSmokeWindow(window, filename: "mighty-graph.png").path
            guard let expand = node(window, identifier: "mighty-expand-\(mainID)") else { throw MightyError("그래프 카드 확장 버튼이 없습니다.") }
            press(expand)
            try await store.waitForSmoke(timeout: 3) { label(node(window, identifier: "mighty-expand-\(mainID)")).contains("접기") }
            report["nativeCardExpansion"] = true
            if let collapse = node(window, identifier: "mighty-expand-\(mainID)") { press(collapse) }
            try await store.waitForSmoke(timeout: 3) { label(node(window, identifier: "mighty-expand-\(mainID)")).contains("더 보기") }
            report["nativeCardCollapse"] = true
            guard let zoom = node(window, identifier: "mighty-zoom-out-graph-fixture") else { throw MightyError("그래프 축소 버튼이 없습니다.") }
            let zoomAnchor = CGPoint(x: (canvas.viewportSize.width / 2 - canvas.panOffset.x) / canvas.zoom,
                                     y: (canvas.viewportSize.height / 2 - canvas.panOffset.y) / canvas.zoom)
            press(zoom)
            try await store.waitForSmoke(timeout: 3) { label(node(window, identifier: "mighty-zoom-reset-graph-fixture")).contains("90%") }
            try await store.waitForSmoke(timeout: 3) {
                abs(canvas.zoom - 0.9) < 0.001
                    && abs((canvas.viewportSize.width / 2 - canvas.panOffset.x) / canvas.zoom - zoomAnchor.x) < 1
                    && abs((canvas.viewportSize.height / 2 - canvas.panOffset.y) / canvas.zoom - zoomAnchor.y) < 1
            }
            report["zoomPreservesViewportCenter"] = true
            report["nativeZoom"] = true
            report["zoomScreenshot"] = try store.captureSmokeWindow(window, filename: "mighty-graph-zoom.png").path
            for percentage in [80, 70, 60, 50] {
                if let decrease = node(window, identifier: "mighty-zoom-out-graph-fixture") { press(decrease) }
                try await store.waitForSmoke(timeout: 3) { label(node(window, identifier: "mighty-zoom-reset-graph-fixture")).contains("\(percentage)%") }
            }
            canvas.setPanOffset(.zero)
            try await Task.sleep(for: .milliseconds(80))
            report["overviewScreenshot"] = try store.captureSmokeWindow(window, filename: "mighty-graph-overview.png").path
            fixture.runs[1].status = "completed"
            fixture.runs[1].agents[0].status = "completed"
            fixture.runs[1].rootEntries = [answer]
            fixture.runs[1].resultEntries = [answer]
            fixture.running = false
            fixture.draft = "다음 요청 초안"
            let settled = MightyGraphLayout.make(runs: fixture.runs, draft: fixture.draft, running: false, expanded: [])
            try verifyGeometry(settled)
            guard settled.nodes.contains(where: { $0.content == .result(1) }), settled.nodes.last?.content == .draft,
                  let nextRoot = settled.nodes.first(where: { $0.content == .request(1) }),
                  let priorResult = settled.nodes.first(where: { $0.content == .result(0) }),
                  nextRoot.frame.minY > priorResult.frame.maxY else { throw MightyError("완료·결과·다음 요청의 순서가 잘못됐습니다.") }
            report["sequentialHistoryAndNextDraft"] = true
            guard let workspace = store.activeWorkspace else { throw MightyError("모드 전환 검증 워크스페이스가 없습니다.") }
            var session = RunSession(id: "graph-mode-fixture", workspaceId: workspace.id, title: "모드 전환 검증", provider: "claude", status: "completed", logs: [answer])
            session.graphRuns = fixture.runs
            store.snapshot.sessions.append(session)
            store.drafts[session.id] = "모드 전환 전 초안"
            window.contentView = NSHostingView(rootView: ModeFixtureView(store: store, sessionID: session.id))
            try await store.waitForSmoke(timeout: 3) {
                window.contentView.map { textViews(in: $0).contains(where: { $0.isEditable }) } == true
            }
            guard let nativeInput = window.contentView.flatMap({ textViews(in: $0).first(where: { $0.isEditable }) }),
                  window.makeFirstResponder(nativeInput) else { throw MightyError("모드 전환용 네이티브 입력창을 찾지 못했습니다.") }
            nativeInput.insertText("한글 초안 · 그래프 전환", replacementRange: NSRange(location: 0, length: nativeInput.string.utf16.count))
            try await store.waitForSmoke(timeout: 3) { store.drafts[session.id] == "한글 초안 · 그래프 전환" }
            guard let mighty = node(window, identifier: "agent-mode-mighty-\(session.id)") else { throw MightyError("마이티 모드 선택 버튼이 없습니다.") }
            press(mighty)
            try await store.waitForSmoke(timeout: 3) { label(node(window, identifier: "mighty-draft-\(session.id)")).contains("한글 초안") }
            guard window.contentView.map({ textViews(in: $0).contains(where: { $0 === nativeInput }) }) == true,
                  nativeInput.string == "한글 초안 · 그래프 전환",
                  label(node(window, identifier: "mighty-draft-\(session.id)")).contains("한글 초안") else { throw MightyError("마이티 전환이 입력창이나 다음 요청 초안을 바꿨습니다.") }
            report["modeScreenshot"] = try store.captureSmokeWindow(window, filename: "mighty-mode-pane.png").path
            let savedSize = MightyGraphBlockSize(width: 680, height: 230)
            store.setGraphBlockSize(session.id, nodeID: "pending-input", size: savedSize)
            try await store.waitForSmoke(timeout: 3) {
                window.contentView.flatMap { camera(in: $0) }?.layoutFrames.first { $0.0 == "pending-input" }?.1.size == CGSize(width: 680, height: 230)
            }
            guard let normal = node(window, identifier: "agent-mode-default-\(session.id)") else { throw MightyError("기본 모드 선택 버튼이 없습니다.") }
            press(normal)
            try await store.waitForSmoke(timeout: 3) { node(window, identifier: "mighty-graph-\(session.id)") == nil }
            guard window.contentView.map({ textViews(in: $0).contains(where: { $0 === nativeInput }) }) == true,
                  store.drafts[session.id] == "한글 초안 · 그래프 전환",
                  store.snapshot.sessions.first(where: { $0.id == session.id })?.logs == session.logs,
                  store.snapshot.sessions.first(where: { $0.id == session.id })?.graphRuns == session.graphRuns else { throw MightyError("기본 모드 복귀가 대화·그래프 기록 또는 초안을 지웠습니다.") }
            report["modeTogglePreservesNativeComposerAndHistory"] = true
            if let mighty = node(window, identifier: "agent-mode-mighty-\(session.id)") { press(mighty) }
            try await store.waitForSmoke(timeout: 3) {
                window.contentView.flatMap { camera(in: $0) }?.layoutFrames.first { $0.0 == "pending-input" }?.1.size == CGSize(width: 680, height: 230)
            }
            guard store.snapshot.sessions.first(where: { $0.id == session.id })?.graphBlockSizes?["pending-input"] == savedSize else { throw MightyError("모드를 전환하면서 저장한 크기가 사라졌습니다.") }
            report["sessionSizesSurviveModeRemount"] = true
            store.setGraphBlockSize(session.id, nodeID: "pending-input", size: nil)
            try await store.waitForSmoke(timeout: 3) {
                window.contentView.flatMap { camera(in: $0) }?.layoutFrames.first { $0.0 == "pending-input" }?.1.size == CGSize(width: 500, height: 140)
            }
            report["resetRestoresDefaultSize"] = true
            report["pendingDraftMirrorsComposer"] = true
            let initial = MightyGraphLayout.make(runs: [], draft: "", running: false, expanded: [])
            guard initial.nodes.count == 1, initial.nodes.first?.content == .draft else { throw MightyError("빈 대화의 첫 입력 블록이 없습니다.") }
            report["initialPendingInput"] = true
            fixture.runs = (0..<24).map { index in
                var run = first; run.id = "long-history-\(index)"; return run
            }
            fixture.draft = ""; fixture.running = false
            window.contentView = NSHostingView(rootView: GraphFixtureView(fixture: fixture))
            try await store.waitForSmoke(timeout: 3) { node(window, identifier: "mighty-node-pending-input") != nil }
            guard let longContent = window.contentView else { throw MightyError("긴 그래프 검증 화면이 없습니다.") }
            let mounted = textViews(in: longContent).count
            let mountedLongCards = mountedCards(window)
            // Two-sided: the block the camera is aimed at is really on screen —
            // a camera left outside the document culls every card and a blank
            // canvas would otherwise pass — while the far history is not.
            let oldestLongID = MightyGraphBlockSize.nodeID(runID: "long-history-0", suffix: "request")
            guard mounted > 0, mountedLongCards.contains(MightyGraphCamera.pendingNodeID),
                  !mountedLongCards.contains(oldestLongID) else { throw MightyError("긴 기록의 화면 안팎 그래프 카드 구성이 올바르지 않습니다.") }
            guard mounted < 24 else { throw MightyError("화면 밖의 그래프까지 네이티브 출력창을 만들었습니다.") }
            report["longHistoryVirtualized"] = true
            report["longHistoryMountedTranscripts"] = mounted
            report["longHistoryMountedCards"] = mountedLongCards.count
            report["longHistoryRunCount"] = fixture.runs.count
            let newRun = MightyGraphRun(id: "new-visible-request", input: "새 요청은 현재 위치로 이동합니다.", status: "running")
            fixture.running = true
            fixture.runs.append(newRun)
            let newID = MightyGraphLayout.nodeID(newRun, suffix: "request")
            try await store.waitForSmoke(timeout: 3) {
                guard let camera = camera(in: longContent), let request = frame(node(window, identifier: "mighty-request-\(newID)")) else { return false }
                let viewport = window.convertToScreen(camera.convert(camera.bounds, to: nil))
                report["newRequestCameraGeometry"] = ["request": NSStringFromRect(request), "viewport": NSStringFromRect(viewport), "cameraOffset": NSStringFromPoint(camera.panOffset)]
                return viewport.insetBy(dx: -2, dy: -2).contains(request)
            }
            report["newRequestMovesOnceAfterDocumentGrowth"] = true
            // Two root-level branches make this tree wider than a request card.
            // On the fixed centreline the request card above them keeps its x,
            // instead of every card in the document sliding sideways.
            guard let beforeGrowth = frame(node(window, identifier: "mighty-request-\(newID)")) else { throw MightyError("새 요청 카드의 화면 위치를 읽지 못했습니다.") }
            let branchEntry = LogEntry(id: "branch-progress", kind: "assistant", text: "가지가 늘어났습니다.", provider: "claude")
            fixture.runs[fixture.runs.count - 1].agents = [
                MightyGraphAgent(id: "branch-left", title: "왼쪽 가지", input: "왼쪽 확인", status: "running", entries: [branchEntry]),
                MightyGraphAgent(id: "branch-right", title: "오른쪽 가지", input: "오른쪽 확인", status: "running", entries: [branchEntry])
            ]
            let branchID = MightyGraphLayout.nodeID(newRun, suffix: "agent:branch-left")
            try await store.waitForSmoke(timeout: 3) { node(window, identifier: "mighty-node-\(branchID)") != nil }
            guard let afterGrowth = frame(node(window, identifier: "mighty-request-\(newID)")) else { throw MightyError("가지가 늘어난 뒤 요청 카드가 사라졌습니다.") }
            report["rootBranchGrowthGeometry"] = ["before": NSStringFromRect(beforeGrowth), "after": NSStringFromRect(afterGrowth)]
            guard abs(afterGrowth.minX - beforeGrowth.minX) < 0.5 else { throw MightyError("새 하위 블록이 이미 보이던 요청 카드를 옆으로 밀었습니다.") }
            report["rootBranchGrowthKeepsRequestX"] = true
            // A history trim drops the OLDEST runs while the last run id stays
            // the same. Every surviving card moves up; only a re-aim follows,
            // and without one the committed camera sits past the document.
            let beforeTrim = fixture.runs.map(\.id)
            fixture.runs.removeFirst(8)
            guard fixture.runs.last?.id == beforeTrim.last, fixture.runs.count < beforeTrim.count else { throw MightyError("기록 정리 검증이 마지막 요청까지 지웠습니다.") }
            try await store.waitForSmoke(timeout: 3) {
                guard let camera = camera(in: longContent), let request = frame(node(window, identifier: "mighty-request-\(newID)")) else { return false }
                let viewport = window.convertToScreen(camera.convert(camera.bounds, to: nil))
                let cards = mountedCards(window)
                report["historyTrimCameraGeometry"] = ["request": NSStringFromRect(request), "viewport": NSStringFromRect(viewport),
                                                       "cameraOffset": NSStringFromPoint(camera.panOffset), "mountedCards": cards.count]
                return cards.contains(newID) && viewport.insetBy(dx: -2, dy: -2).contains(request)
            }
            report["historyTrimReAimsAtTheAnchoredBlock"] = true
            // The same run ids with shorter trees: nothing the run-id rule can
            // see, yet every later card moves up under a camera nobody touched.
            // The cause-independent net is what brings the anchor back.
            guard let strandedBefore = camera(in: longContent)?.panOffset else { throw MightyError("짧아진 레이아웃 검증용 캔버스가 없습니다.") }
            // One assignment, one layout pass: the net must see the whole loss at once.
            var shortened = fixture.runs
            for index in shortened.indices where shortened[index].id != newRun.id { shortened[index].agents = [] }
            fixture.runs = shortened
            try await store.waitForSmoke(timeout: 3) {
                guard let strandedCamera = camera(in: longContent),
                      let request = frame(node(window, identifier: "mighty-request-\(newID)")) else { return false }
                let viewport = window.convertToScreen(strandedCamera.convert(strandedCamera.bounds, to: nil))
                report["strandedLayoutCameraGeometry"] = ["before": NSStringFromPoint(strandedBefore), "after": NSStringFromPoint(strandedCamera.panOffset),
                                                          "request": NSStringFromRect(request), "viewport": NSStringFromRect(viewport)]
                // Already true before the change: only a camera that moved proves the net.
                return strandedCamera.panOffset != strandedBefore && mountedCards(window).contains(newID) && viewport.insetBy(dx: -2, dy: -2).contains(request)
            }
            report["shorterLayoutReAimsWithoutARunIDChange"] = true
            report["nodeCount"] = settled.nodes.count
            report["edgeCount"] = settled.edges.count
            let interaction = await MightyGraphInteractionDiagnostics.run(store: store)
            report["interaction"] = interaction
            guard interaction["passed"] as? Bool == true else { throw MightyError("그래프 네이티브 스크롤·선택·진행 표시 검증에 실패했습니다.") }
            report["passed"] = true
        } catch {
            report["error"] = error.localizedDescription
            if let content = window.contentView {
                report["nativeViewKinds"] = nativeViewKinds(in: content)
                if let camera = camera(in: content) {
                    report["cameraDiagnostic"] = camera.diagnostic
                    report["failureCameraGeometry"] = ["viewport": NSStringFromRect(camera.bounds), "cameraOffset": NSStringFromPoint(camera.panOffset)]
                }
            }
            if let url = try? store.captureSmokeWindow(window, filename: "mighty-graph-failure.png") { report["failureScreenshot"] = url.path }
        }
        return report
    }

    private static func verifyGeometry(_ layout: MightyGraphLayout) throws {
        guard Set(layout.nodes.map(\.id)).count == layout.nodes.count else { throw MightyError("그래프 노드 ID가 중복됐습니다.") }
        // The diagram starts at its own leading edge, which a tree wider than a
        // request card puts left of zero; the drawn container starts there too.
        let canvas = CGRect(x: layout.originX, y: 0, width: layout.size.width, height: layout.size.height)
        for (index, node) in layout.nodes.enumerated() {
            guard canvas.contains(node.frame) else { throw MightyError("그래프 카드가 캔버스를 벗어났습니다.") }
            for other in layout.nodes.dropFirst(index + 1) where node.frame.intersects(other.frame) { throw MightyError("그래프 카드가 겹칩니다: \(node.id) / \(other.id)") }
        }
        for edge in layout.edges {
            let route = layout.route(edge)
            guard route.count == 4, route.last!.y > route.first!.y else { throw MightyError("그래프 연결선이 역행하거나 연결 대상이 없습니다.") }
            for node in layout.nodes where node.id != edge.source && node.id != edge.target {
                let interior = node.frame.insetBy(dx: 1, dy: 1)
                for index in 1..<route.count {
                    let a = route[index - 1], b = route[index]
                    let segment = CGRect(x: min(a.x, b.x) - 0.1, y: min(a.y, b.y) - 0.1, width: abs(a.x - b.x) + 0.2, height: abs(a.y - b.y) + 0.2)
                    guard !interior.intersects(segment) else { throw MightyError("연결선이 다른 그래프 카드를 가로지릅니다.") }
                }
            }
        }
    }
    private static func nativeViewKinds(in view: NSView, depth: Int = 0) -> [String] {
        guard depth < 14 else { return [] }
        let name = String(describing: type(of: view)).split(separator: "<", maxSplits: 1).first.map(String.init) ?? "NSView"
        return [String(repeating: " ", count: depth) + name]
            + view.subviews.prefix(20).flatMap { nativeViewKinds(in: $0, depth: depth + 1) }
    }
    private static func camera(in view: NSView) -> MightyGraphInteractionProbe? {
        if let camera = view as? MightyGraphInteractionProbe { return camera }
        return view.subviews.lazy.compactMap { camera(in: $0) }.first
    }
    private static func frame(_ object: NSObject?) -> NSRect? {
        guard let object, object.responds(to: NSSelectorFromString("accessibilityFrame")) else { return nil }
        return (object.value(forKey: "accessibilityFrame") as? NSValue)?.rectValue
    }
    private static func textViews(in view: NSView) -> [NSTextView] {
        (view as? NSTextView).map { [$0] } ?? view.subviews.flatMap { textViews(in: $0) }
    }
    private static func node(_ element: Any, identifier: String, depth: Int = 0) -> NSObject? {
        guard depth < 65, let object = element as? NSObject else { return nil }
        if object.responds(to: NSSelectorFromString("accessibilityIdentifier")), object.value(forKey: "accessibilityIdentifier") as? String == identifier { return object }
        guard object.responds(to: NSSelectorFromString("accessibilityChildren")) else { return nil }
        for child in object.value(forKey: "accessibilityChildren") as? [Any] ?? [] {
            if let found = node(child, identifier: identifier, depth: depth + 1) { return found }
        }
        return nil
    }
    /// Cards render as `Color.clear` once the camera leaves them behind, so
    /// this names what is actually on screen rather than what the layout holds.
    private static func mountedCards(_ element: Any, depth: Int = 0) -> Set<String> {
        guard depth < 65, let object = element as? NSObject else { return [] }
        var result = Set<String>()
        if object.responds(to: NSSelectorFromString("accessibilityIdentifier")),
           let identifier = object.value(forKey: "accessibilityIdentifier") as? String, identifier.hasPrefix("mighty-node-") {
            result.insert(String(identifier.dropFirst("mighty-node-".count)))
        }
        guard object.responds(to: NSSelectorFromString("accessibilityChildren")) else { return result }
        for child in object.value(forKey: "accessibilityChildren") as? [Any] ?? [] { result.formUnion(mountedCards(child, depth: depth + 1)) }
        return result
    }
    private static func label(_ object: NSObject?) -> String {
        guard let object else { return "" }
        return ["accessibilityLabel", "accessibilityTitle", "accessibilityValue"].compactMap { key in
            object.responds(to: NSSelectorFromString(key)) ? object.value(forKey: key) as? String : nil
        }.joined(separator: " ")
    }
    private static func press(_ object: NSObject) {
        let selector = NSSelectorFromString("accessibilityPerformPress")
        guard object.responds(to: selector), let implementation = object.method(for: selector) else { return }
        typealias Press = @convention(c) (AnyObject, Selector) -> Bool
        _ = unsafeBitCast(implementation, to: Press.self)(object, selector)
    }
    @MainActor private final class GraphFixture: ObservableObject {
        @Published var runs: [MightyGraphRun]
        @Published var draft = ""
        @Published var running = true
        init(runs: [MightyGraphRun]) { self.runs = runs }
    }
    private struct ModeFixtureView: View {
        @ObservedObject var store: AppStore
        let sessionID: String
        var body: some View {
            if let session = store.snapshot.sessions.first(where: { $0.id == sessionID }) {
                SessionPaneView(session: session).environmentObject(store).id(sessionID)
            }
        }
    }
    private struct GraphFixtureView: View {
        @ObservedObject var fixture: GraphFixture
        var body: some View {
            MightyGraphView(sessionID: "graph-fixture", provider: "claude", runs: fixture.runs, draft: fixture.draft, running: fixture.running, onFocus: {})
        }
    }
}
