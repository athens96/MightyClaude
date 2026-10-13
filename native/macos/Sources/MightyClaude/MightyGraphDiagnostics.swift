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
            // The view's own layout: each request over its 실행 block.
            let layout = MightyGraphLayout.make(runs: fixture.runs, draft: "", running: true, expanded: [], splitsExecution: true)
            try verifyGeometry(layout)
            guard layout.nodes.count == 10, !layout.nodes.contains(where: { $0.content == .draft }),
                  !layout.nodes.contains(where: { $0.content == .result(1) }),
                  layout.nodes.contains(where: { $0.content == .result(0) }) else { throw MightyError("진행 중인 요청에 최종 결과나 빈 다음 입력이 표시됩니다.") }
            let research = MightyGraphLayout.nodeID(first, suffix: "agent:research")
            let verify = MightyGraphLayout.nodeID(first, suffix: "agent:verify")
            let nested = MightyGraphLayout.nodeID(first, suffix: "agent:nested")
            guard let left = layout.nodes.first(where: { $0.id == research }), let right = layout.nodes.first(where: { $0.id == verify }),
                  left.frame.minY == right.frame.minY, left.frame.maxX < right.frame.minX,
                  layout.edges.contains(where: { $0.source == research && $0.target == nested }) else { throw MightyError("형제·중첩 에이전트 연결 구조가 올바르지 않습니다.") }
            report["horizontalSiblingsAndNestedEdges"] = true
            let firstRequest = MightyGraphLayout.nodeID(first, suffix: "request")
            let firstWork = MightyGraphLayout.nodeID(first, suffix: MightyGraphLayout.workSuffix)
            guard layout.edges.contains(where: { $0.source == firstRequest && $0.target == firstWork }),
                  layout.edges.contains(where: { $0.source == firstWork && $0.target == research }),
                  !layout.edges.contains(where: { $0.source == firstRequest && $0.target == research }) else { throw MightyError("하위 에이전트가 실행 블록이 아닌 곳에서 갈라집니다.") }
            report["subAgentsBranchFromExecution"] = true
            report["noEarlyResultOrEmptyPending"] = true
            var parentEnded = second
            parentEnded.status = "completed"
            guard !MightyGraphLayout.finished(parentEnded) else { throw MightyError("하위 에이전트가 대기 중인데 전체 작업을 완료로 표시합니다.") }
            let expanded = MightyGraphLayout.make(runs: fixture.runs, draft: "", running: true, expanded: [research], splitsExecution: true)
            try verifyGeometry(expanded)
            guard expanded.size.height > layout.size.height else { throw MightyError("카드 확장이 뒤쪽 내용을 밀지 않았습니다.") }
            report["expandedCardsNeverOverlap"] = true

            let resizedIDs = [MightyGraphLayout.nodeID(first, suffix: "request"), research, verify, nested,
                              MightyGraphLayout.nodeID(first, suffix: "result"), "pending-input", firstWork]
            let sizes = Dictionary(uniqueKeysWithValues: resizedIDs.enumerated().map { index, id in
                (id, MightyGraphBlockSize(width: index.isMultiple(of: 2) ? 1100 : 320,
                                          height: index.isMultiple(of: 2) ? 700 : 160))
            })
            let resized = MightyGraphLayout.make(runs: fixture.runs, draft: "다음 요청", running: true, expanded: [], blockSizes: sizes, splitsExecution: true)
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
            try await store.waitForSmoke(timeout: 3, step: "graph canvas mounted") { window.contentView.flatMap { camera(in: $0) } != nil }
            guard let content = window.contentView else { throw MightyError("그래프 캔버스가 없습니다.") }
            try await store.waitForSmoke(timeout: 3, step: "graph viewport measured") { (camera(in: content)?.viewportSize.width ?? 0) > 0 }
            guard let canvas = camera(in: content) else { throw MightyError("고정 다이어그램 뷰포트를 찾지 못했습니다.") }
            guard canvas.enclosingScrollView == nil else { throw MightyError("다이어그램이 여전히 바깥 문서 스크롤에 포함되어 있습니다.") }
            report["fixedViewportWithoutOuterDocumentScroll"] = true
            try await store.waitForSmoke(timeout: 3, step: "canvas exposed to accessibility") { node(window, identifier: "mighty-graph-canvas-graph-fixture") != nil }
            report["nativeCanvasAccessibilityExposed"] = true
            // The camera opens following: the newest request's lowest block right above the composer.
            try await store.waitForSmoke(timeout: 3, step: "newest block followed above the composer at open",
                                         detail: { followDetail(canvas, fixture, window) }) {
                report["initialCameraGeometry"] = ["followed": followGeometry(canvas, fixture), "cameraOffset": NSStringFromPoint(canvas.panOffset)]
                // The followed block is drawn; the request above it may sit mostly past
                // the top, where it has no accessibility element to find.
                return followsNewest(canvas, fixture) && followedMounted(canvas, window)
            }
            report["newestBlockInitiallyAboveComposer"] = true
            report["latestScreenshot"] = try store.captureSmokeWindow(window, filename: "mighty-graph-latest.png").path
            // Move the camera to earlier history, then stream a log. A content
            // update must not recenter the fixed viewport.
            canvas.setPanOffset(.zero)
            try await store.waitForSmoke(timeout: 3, step: "manual pan to the origin committed") { canvas.panOffset == .zero }
            let manualOrigin = canvas.panOffset
            fixture.runs[1].rootEntries.append(LogEntry(id: "live-progress", kind: "assistant", text: "진행 상황이 갱신되었습니다.", provider: "claude"))
            try await Task.sleep(for: .milliseconds(100))
            guard abs(canvas.panOffset.y - manualOrigin.y) <= 2 && abs(canvas.panOffset.x - manualOrigin.x) <= 2 else { throw MightyError("출력 갱신이 사용자의 그래프 스크롤을 바꿨습니다.") }
            report["streamingPreservesManualScroll"] = true
            try await store.waitForSmoke(timeout: 3, step: "streamed reply rendered in the graph") {
                textViews(in: content).contains(where: { $0.string.contains("검증 완료") && $0.string.contains("let result = 42") })
            }
            let editors = textViews(in: content)
            guard editors.contains(where: { $0.string.contains("검증 완료") && $0.string.contains("let result = 42") }),
                  editors.contains(where: { $0.string.contains("swift test") }),
                  editors.allSatisfy({ !$0.isEditable && $0.isSelectable }) else { throw MightyError("그래프의 마크다운·도구 출력이 기존 선택 가능한 렌더러와 다릅니다.") }
            guard !editors.compactMap({ $0 as? AgentTranscriptTextView }).contains(where: { $0.string.contains(first.input) }) else { throw MightyError("요청이 고정 입력 영역과 출력 기록에 중복 표시됩니다.") }
            report["requestNotDuplicatedInOutput"] = true
            // The request block holds its input alone; the main agent's records are in its 실행 block.
            let transcripts = Set(editors.compactMap { ($0 as? AgentTranscriptTextView)?.accessibilityIdentifier() })
            guard transcripts.contains("transcript-graph-graph-fixture-\(firstWork)"),
                  !transcripts.contains("transcript-graph-graph-fixture-\(mainID)") else { throw MightyError("요청 블록에 에이전트 기록이 있거나 실행 블록에 기록이 없습니다.") }
            report["requestHoldsInputExecutionHoldsRecords"] = true
            report["nativeMarkdownAndToolRenderer"] = true
            report["nativeTranscriptCount"] = editors.count
            report["screenshot"] = try store.captureSmokeWindow(window, filename: "mighty-graph.png").path
            // The header's 펼치기/접기, run as its button runs it (`diagnosticActions`): the
            // card must really be laid out taller, push what follows down, and overlap nothing.
            func mainFrame() -> CGRect? { canvas.layoutFrames.first { $0.0 == mainID }?.1 }
            let frameDetail = { "main=\(mainFrame().map { "\($0)" } ?? "nil") overlap=\(overlappingPair(canvas) ?? "none")" }
            guard let collapsed = mainFrame() else { throw MightyError("그래프 카드 확장 대상이 레이아웃에 없습니다.") }
            canvas.diagnosticActions.toggleExpanded(mainID)
            try await store.waitForSmoke(timeout: 3, step: "card expanded", detail: frameDetail) {
                mainFrame()?.height == MightyGraphLayout.requestHeight(expanded: true) && mainFrame()?.minY == collapsed.minY && overlappingPair(canvas) == nil
            }
            report["nativeCardExpansion"] = true
            canvas.diagnosticActions.toggleExpanded(mainID)
            try await store.waitForSmoke(timeout: 3, step: "card collapsed", detail: frameDetail) {
                mainFrame() == collapsed && overlappingPair(canvas) == nil
            }
            report["nativeCardCollapse"] = true
            let zoomAnchor = CGPoint(x: (canvas.viewportSize.width / 2 - canvas.panOffset.x) / canvas.zoom,
                                     y: (canvas.viewportSize.height / 2 - canvas.panOffset.y) / canvas.zoom)
            // The header's zoom-out button, run as the button runs it.
            canvas.diagnosticActions.zoomOut()
            try await store.waitForSmoke(timeout: 3, step: "zoom keeps the viewport centre") {
                abs(canvas.zoom - 0.9) < 0.001
                    && abs((canvas.viewportSize.width / 2 - canvas.panOffset.x) / canvas.zoom - zoomAnchor.x) < 1
                    && abs((canvas.viewportSize.height / 2 - canvas.panOffset.y) / canvas.zoom - zoomAnchor.y) < 1
            }
            report["zoomPreservesViewportCenter"] = true
            report["nativeZoom"] = true
            report["zoomScreenshot"] = try store.captureSmokeWindow(window, filename: "mighty-graph-zoom.png").path
            for percentage in [80, 70, 60, 50] {
                canvas.diagnosticActions.zoomOut()
                try await store.waitForSmoke(timeout: 3, step: "zoom step to \(percentage)%", detail: { "zoom=\(canvas.zoom)" }) {
                    abs(canvas.zoom - CGFloat(percentage) / 100) < 0.001
                }
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
            let settled = MightyGraphLayout.make(runs: fixture.runs, draft: fixture.draft, running: false, expanded: [], splitsExecution: true)
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
            try await store.waitForSmoke(timeout: 3, step: "mode fixture composer mounted") {
                window.contentView.map { textViews(in: $0).contains(where: { $0.isEditable }) } == true
            }
            guard let nativeInput = window.contentView.flatMap({ textViews(in: $0).first(where: { $0.isEditable }) }),
                  window.makeFirstResponder(nativeInput) else { throw MightyError("모드 전환용 네이티브 입력창을 찾지 못했습니다.") }
            nativeInput.insertText("한글 초안 · 그래프 전환", replacementRange: NSRange(location: 0, length: nativeInput.string.utf16.count))
            try await store.waitForSmoke(timeout: 3, step: "draft typed into the composer") { store.drafts[session.id] == "한글 초안 · 그래프 전환" }
            // The pane's mode chip, run as its button runs it.
            store.selectSession(session.id); store.setAgentViewMode(session.id, mode: "mighty")
            // The camera follows the newest result; the draft block below it is panned into view.
            try await store.waitForSmoke(timeout: 3, step: "Mighty mode shows the draft block") {
                showDraft(window)
                return label(node(window, identifier: "mighty-draft-\(session.id)")).contains("한글 초안")
            }
            guard window.contentView.map({ textViews(in: $0).contains(where: { $0 === nativeInput }) }) == true,
                  nativeInput.string == "한글 초안 · 그래프 전환",
                  label(node(window, identifier: "mighty-draft-\(session.id)")).contains("한글 초안") else { throw MightyError("마이티 전환이 입력창이나 다음 요청 초안을 바꿨습니다.") }
            report["modeScreenshot"] = try store.captureSmokeWindow(window, filename: "mighty-mode-pane.png").path
            let savedSize = MightyGraphBlockSize(width: 680, height: 230)
            store.setGraphBlockSize(session.id, nodeID: "pending-input", size: savedSize)
            try await store.waitForSmoke(timeout: 3, step: "saved draft block size applied") {
                window.contentView.flatMap { camera(in: $0) }?.layoutFrames.first { $0.0 == "pending-input" }?.1.size == CGSize(width: 680, height: 230)
            }
            store.selectSession(session.id); store.setAgentViewMode(session.id, mode: "default")
            // The diagram's canvas is gone from the pane (a native view, found without accessibility).
            try await store.waitForSmoke(timeout: 3, step: "default mode hides the graph") { window.contentView.flatMap { camera(in: $0) } == nil }
            guard window.contentView.map({ textViews(in: $0).contains(where: { $0 === nativeInput }) }) == true,
                  store.drafts[session.id] == "한글 초안 · 그래프 전환",
                  store.snapshot.sessions.first(where: { $0.id == session.id })?.logs == session.logs,
                  store.snapshot.sessions.first(where: { $0.id == session.id })?.graphRuns == session.graphRuns else { throw MightyError("기본 모드 복귀가 대화·그래프 기록 또는 초안을 지웠습니다.") }
            report["modeTogglePreservesNativeComposerAndHistory"] = true
            store.selectSession(session.id); store.setAgentViewMode(session.id, mode: "mighty")
            try await store.waitForSmoke(timeout: 3, step: "saved size survives the mode remount") {
                window.contentView.flatMap { camera(in: $0) }?.layoutFrames.first { $0.0 == "pending-input" }?.1.size == CGSize(width: 680, height: 230)
            }
            guard store.snapshot.sessions.first(where: { $0.id == session.id })?.graphBlockSizes?["pending-input"] == savedSize else { throw MightyError("모드를 전환하면서 저장한 크기가 사라졌습니다.") }
            report["sessionSizesSurviveModeRemount"] = true
            store.setGraphBlockSize(session.id, nodeID: "pending-input", size: nil)
            try await store.waitForSmoke(timeout: 3, step: "reset restores the draft block size") {
                window.contentView.flatMap { camera(in: $0) }?.layoutFrames.first { $0.0 == "pending-input" }?.1.size == CGSize(width: 500, height: 140)
            }
            report["resetRestoresDefaultSize"] = true
            report["pendingDraftMirrorsComposer"] = true
            let initial = MightyGraphLayout.make(runs: [], draft: "", running: false, expanded: [], splitsExecution: true)
            guard initial.nodes.count == 1, initial.nodes.first?.content == .draft else { throw MightyError("빈 대화의 첫 입력 블록이 없습니다.") }
            report["initialPendingInput"] = true
            fixture.runs = (0..<24).map { index in
                var run = first; run.id = "long-history-\(index)"; return run
            }
            fixture.draft = ""; fixture.running = false
            window.contentView = NSHostingView(rootView: GraphFixtureView(fixture: fixture))
            let newestLongResult = MightyGraphBlockSize.nodeID(runID: "long-history-23", suffix: "result")
            try await store.waitForSmoke(timeout: 3, step: "long history newest result mounted") { mountedCards(window).contains(newestLongResult) }
            guard let longContent = window.contentView else { throw MightyError("긴 그래프 검증 화면이 없습니다.") }
            let mounted = textViews(in: longContent).count
            let mountedLongCards = mountedCards(window)
            // Two-sided: the block the camera follows is really on screen — a
            // camera left outside the document culls every card and a blank
            // canvas would otherwise pass — while the far history is not.
            let oldestLongID = MightyGraphBlockSize.nodeID(runID: "long-history-0", suffix: "request")
            try await store.waitForSmoke(timeout: 3, step: "long history follows the newest result",
                                         detail: { camera(in: longContent).map { followDetail($0, fixture, window) } ?? "no canvas" }) { camera(in: longContent).map { followsNewest($0, fixture) } == true }
            guard mounted > 0, mountedCards(window).contains(newestLongResult),
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
            try await store.waitForSmoke(timeout: 3, step: "new request followed above the composer",
                                         detail: { camera(in: longContent).map { followDetail($0, fixture, window) } ?? "no canvas" }) {
                guard let camera = camera(in: longContent), let request = frame(node(window, identifier: "mighty-request-\(newID)")) else { return false }
                let viewport = window.convertToScreen(camera.convert(camera.bounds, to: nil))
                report["newRequestCameraGeometry"] = ["request": NSStringFromRect(request), "viewport": NSStringFromRect(viewport), "cameraOffset": NSStringFromPoint(camera.panOffset),
                                                      "followed": followGeometry(camera, fixture)]
                // A send is followed: its 실행 block's bottom right above the composer, the request above it.
                return followsNewest(camera, fixture) && viewport.insetBy(dx: -2, dy: -2).contains(request)
            }
            report["newRequestFollowedAboveComposer"] = true
            // Two root-level branches make this tree wider than a request card.
            // On the fixed centreline the request card above them keeps its x,
            // instead of every card in the document sliding sideways.
            // Measured in the canvas (the layout through the camera), not through
            // accessibility: once the branches are followed the request card sits
            // mostly above the canvas, where it has no accessibility element.
            func requestFrame() -> CGRect? { camera(in: longContent)?.frames.first { $0.0 == newID }?.1 }
            guard let beforeGrowth = requestFrame() else { throw MightyError("새 요청 카드의 화면 위치를 읽지 못했습니다.") }
            let branchEntry = LogEntry(id: "branch-progress", kind: "assistant", text: "가지가 늘어났습니다.", provider: "claude")
            fixture.runs[fixture.runs.count - 1].agents = [
                MightyGraphAgent(id: "branch-left", title: "왼쪽 가지", input: "왼쪽 확인", status: "running", entries: [branchEntry]),
                MightyGraphAgent(id: "branch-right", title: "오른쪽 가지", input: "오른쪽 확인", status: "running", entries: [branchEntry])
            ]
            let branchID = MightyGraphLayout.nodeID(newRun, suffix: "agent:branch-left")
            try await store.waitForSmoke(timeout: 3, step: "new branches mounted") { mountedCards(window).contains(branchID) }
            guard let afterGrowth = requestFrame() else { throw MightyError("가지가 늘어난 뒤 요청 카드가 사라졌습니다.") }
            report["rootBranchGrowthGeometry"] = ["before": NSStringFromRect(beforeGrowth), "after": NSStringFromRect(afterGrowth)]
            guard abs(afterGrowth.minX - beforeGrowth.minX) < 0.5 else { throw MightyError("새 하위 블록이 이미 보이던 요청 카드를 옆으로 밀었습니다.") }
            report["rootBranchGrowthKeepsRequestX"] = true
            // The new sub-agents are followed: the camera moves down, never sideways.
            try await store.waitForSmoke(timeout: 3, step: "new branches followed above the composer",
                                         detail: { camera(in: longContent).map { followDetail($0, fixture, window) } ?? "no canvas" }) { camera(in: longContent).map { followsNewest($0, fixture) } == true }
            guard let followedGrowth = requestFrame(), abs(followedGrowth.minX - beforeGrowth.minX) < 0.5 else {
                throw MightyError("새 하위 블록을 따라가며 카메라가 옆으로 움직였습니다. before=\(beforeGrowth) after=\(requestFrame().map { "\($0)" } ?? "nil")")
            }
            report["newBlocksFollowedAboveComposer"] = true
            // A history trim drops the OLDEST runs while the last run id stays
            // the same. Every surviving card moves up; only a re-aim follows,
            // and without one the committed camera sits past the document.
            let beforeTrim = fixture.runs.map(\.id)
            fixture.runs.removeFirst(8)
            guard fixture.runs.last?.id == beforeTrim.last, fixture.runs.count < beforeTrim.count else { throw MightyError("기록 정리 검증이 마지막 요청까지 지웠습니다.") }
            // A following camera keeps its block right above the composer through the trim.
            // Judged only once the trim reached the canvas: before that the camera is still
            // aligned to the untrimmed layout, and the check would pass on the old frames.
            let trimmedID = MightyGraphBlockSize.nodeID(runID: beforeTrim[0], suffix: "request")
            try await store.waitForSmoke(timeout: 3, step: "followed block kept through the history trim",
                                         detail: { camera(in: longContent).map { followDetail($0, fixture, window) + " trimmedStillLaidOut=\($0.layoutFrames.contains { $0.0 == trimmedID })" } ?? "no canvas" }) {
                guard let camera = camera(in: longContent), !camera.layoutFrames.contains(where: { $0.0 == trimmedID }) else { return false }
                let cards = mountedCards(window)
                report["historyTrimCameraGeometry"] = ["followed": followGeometry(camera, fixture),
                                                       "cameraOffset": NSStringFromPoint(camera.panOffset), "mountedCards": cards.count]
                return followsNewest(camera, fixture) && followedMounted(camera, window)
            }
            report["historyTrimKeepsTheFollowedBlock"] = true
            // The user scrolls up 120pt with a real wheel event over the canvas, as a
            // trackpad does: following stops, and a block that appears meanwhile
            // never pulls them back down.
            guard let reader = camera(in: longContent) else { throw MightyError("사용자 스크롤 검증용 캔버스가 없습니다.") }
            NSApp.activate(ignoringOtherApps: true)
            window.makeKeyAndOrderFront(nil)
            try await store.waitForSmoke(timeout: 3, step: "graph window key for the wheel") { NSApp.isActive && window.isKeyWindow }
            let scrollStart = reader.panOffset
            let wheelPoint = (try? MightyGraphInteractionDiagnostics.backgroundPoint(reader)).map { reader.convert($0, to: nil) }
                ?? reader.convert(NSPoint(x: reader.bounds.midX, y: reader.bounds.midY), to: nil)
            report["userScrollWheel"] = try MightyGraphInteractionDiagnostics.postWheel(window: window, at: wheelPoint, dy: 120)
            func readerState() -> String {
                "camera=\(NSStringFromPoint(reader.panOffset)) start=\(NSStringFromPoint(scrollStart)) following=\(reader.follow?.watch.following.description ?? "nil")"
                    + " movedAt=\(reader.lastUserMoveAt) now=\(ProcessInfo.processInfo.systemUptime) target=\(reader.followTargetID ?? "nil")"
                    + " token=\(reader.targetToken ?? "nil") consumed=\(reader.consumedTargetToken ?? "nil")"
            }
            try await store.waitForSmoke(timeout: 3, step: "wheel scroll up stops following", detail: readerState) {
                abs(reader.panOffset.y - scrollStart.y - 120) < 1 && reader.follow?.watch.following == false
            }
            let readerCamera = reader.panOffset
            fixture.runs[fixture.runs.count - 1].agents.append(MightyGraphAgent(id: "branch-late", title: "늦은 가지", input: "추가 확인", status: "running", entries: [branchEntry]))
            try await store.waitForSmoke(timeout: 3, step: "late branch mounted while scrolled up") { mountedCards(window).contains(MightyGraphLayout.nodeID(newRun, suffix: "agent:branch-late")) }
            // Past the 0.5s user-move grace, so a follow pass would have run by now.
            try await Task.sleep(for: .milliseconds(700))
            guard reader.panOffset == readerCamera else {
                throw MightyError("위로 스크롤한 사용자를 새 블록이 다시 아래로 끌어당겼습니다. before=\(NSStringFromPoint(readerCamera)) " + readerState())
            }
            report["userScrollWinsOverNewBlocks"] = true
            // The same run ids with shorter trees: nothing the run-id rule can
            // see, yet every later card moves up under a camera nobody touched.
            // The cause-independent net is what brings the anchor back.
            guard let strandedBefore = camera(in: longContent)?.panOffset else { throw MightyError("짧아진 레이아웃 검증용 캔버스가 없습니다.") }
            // One assignment, one layout pass: the net must see the whole loss at once.
            var shortened = fixture.runs
            for index in shortened.indices where shortened[index].id != newRun.id { shortened[index].agents = [] }
            fixture.runs = shortened
            // The reader saw most of the new request's 실행 block, so the net brings
            // that block back whole (`MightyGraphCamera.lostFrameIndex`). Whether the
            // request above it fits too depends on the runner's screen height.
            let newWork = MightyGraphLayout.nodeID(newRun, suffix: MightyGraphLayout.workSuffix)
            try await store.waitForSmoke(timeout: 3, step: "stranded net re-aims at the read card",
                                         detail: { "camera=\(camera(in: longContent).map { NSStringFromPoint($0.panOffset) } ?? "nil") before=\(NSStringFromPoint(strandedBefore)) work=\(camera(in: longContent)?.frames.first { $0.0 == newWork }?.1 ?? .null) mounted=\(mountedCards(window).contains(newWork))" }) {
                guard let strandedCamera = camera(in: longContent),
                      let work = strandedCamera.frames.first(where: { $0.0 == newWork })?.1 else { return false }
                report["strandedLayoutCameraGeometry"] = ["before": NSStringFromPoint(strandedBefore), "after": NSStringFromPoint(strandedCamera.panOffset),
                                                          "work": NSStringFromRect(work), "viewport": NSStringFromRect(strandedCamera.bounds)]
                // Already true before the change: only a camera that moved proves the net.
                return strandedCamera.panOffset != strandedBefore && mountedCards(window).contains(newWork)
                    && strandedCamera.bounds.insetBy(dx: -2, dy: -2).contains(work)
            }
            report["shorterLayoutReAimsWithoutARunIDChange"] = true
            report["nodeCount"] = settled.nodes.count
            report["edgeCount"] = settled.edges.count
            let interaction = await MightyGraphInteractionDiagnostics.run(store: store)
            report["interaction"] = interaction
            guard interaction["passed"] as? Bool == true else { throw MightyError("그래프 네이티브 스크롤·선택·진행 표시 검증에 실패했습니다.") }
            let structure = await MightyStructureInteractionDiagnostics.run(store: store)
            report["structure"] = structure
            guard structure["passed"] as? Bool == true else { throw MightyError("구조 보기의 끌기·본문 선택·펼치기 검증에 실패했습니다.") }
            report["passed"] = true
        } catch {
            report["error"] = error.localizedDescription
            report["canvasIdentifiers"] = canvasIdentifiers(window)
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
    /// The block the camera follows sits right above the composer (`MightyGraphFollow`).
    private static func followsNewest(_ camera: MightyGraphInteractionProbe, _ fixture: GraphFixture) -> Bool {
        guard let frame = followedFrame(camera, fixture) else { return false }
        return abs(frame.maxY - (camera.bounds.height - MightyGraphFollow.margin)) < 1
    }
    private static func followedFrame(_ camera: MightyGraphInteractionProbe, _ fixture: GraphFixture) -> CGRect? {
        // The view's own followed block, checked against the rule's pick for the fixture.
        let layout = MightyGraphLayout.make(runs: fixture.runs, draft: fixture.draft, running: fixture.running, expanded: [], splitsExecution: true)
        guard let id = camera.followTargetID, id == MightyGraphFollow.targetID(layout, runs: fixture.runs) else { return nil }
        return camera.frames.first { $0.0 == id }?.1
    }
    /// Two laid-out blocks that overlap, or nil.
    private static func overlappingPair(_ camera: MightyGraphInteractionProbe) -> String? {
        let frames = camera.layoutFrames
        for (index, node) in frames.enumerated() {
            for other in frames.dropFirst(index + 1) where node.1.intersects(other.1) { return node.0 + " / " + other.0 }
        }
        return nil
    }
    private static func followedMounted(_ camera: MightyGraphInteractionProbe, _ window: NSWindow) -> Bool {
        camera.followTargetID.map { mountedCards(window).contains($0) } == true
    }
    /// What a follow check measured, for a timeout's message.
    private static func followDetail(_ camera: MightyGraphInteractionProbe, _ fixture: GraphFixture, _ window: NSWindow) -> String {
        let layout = MightyGraphLayout.make(runs: fixture.runs, draft: fixture.draft, running: fixture.running, expanded: [], splitsExecution: true)
        let frame = camera.followTargetID.flatMap { id in camera.frames.first { $0.0 == id }?.1 }
        let expected = camera.bounds.height - MightyGraphFollow.margin
        return "followed=\(camera.followTargetID ?? "nil") rule=\(MightyGraphFollow.targetID(layout, runs: fixture.runs) ?? "nil")"
            + " bottom=" + (frame.map { "\($0.maxY)" } ?? "nil") + " viewport=\(camera.bounds.height) expected=\(expected)"
            + " mounted=\(followedMounted(camera, window)) camera=" + NSStringFromPoint(camera.panOffset)
    }
    private static func followGeometry(_ camera: MightyGraphInteractionProbe, _ fixture: GraphFixture) -> String {
        "frame=" + (followedFrame(camera, fixture).map(NSStringFromRect) ?? "nil") + " viewport=" + NSStringFromRect(camera.bounds)
    }
    /// Pans the next-request draft block into view once the camera's first
    /// placement landed: a following camera keeps the newest result above the
    /// composer, with the draft block below it.
    private static func showDraft(_ window: NSWindow) {
        guard let camera = window.contentView.flatMap({ camera(in: $0) }), camera.targetToken == camera.consumedTargetToken,
              let draft = camera.frames.first(where: { $0.0 == MightyGraphCamera.pendingNodeID })?.1,
              draft.maxY > camera.bounds.height - MightyGraphFollow.margin else { return }
        camera.setPanOffset(CGPoint(x: camera.panOffset.x, y: camera.panOffset.y - (draft.maxY - camera.bounds.height + MightyGraphFollow.margin)))
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
    /// Visits every accessibility element under `root` until `visit` says stop. The
    /// accessibility children alone are not enough: until an assistive client asks
    /// (a CI runner has none), AppKit leaves a hosting wrapper out of its parent's
    /// accessibility children, and the diagram's canvas and cards live in hosting
    /// views nested inside the pane's. So the native subviews are walked too, as
    /// `AppStore.smokeAccessibilityElement` does; identity tracking visits each once.
    private static func walk(_ root: Any, _ visit: (NSObject) -> Bool) {
        var visited = Set<ObjectIdentifier>()
        func go(_ element: Any, depth: Int) -> Bool {
            if let view = element as? NSView, view.isHiddenOrHasHiddenAncestor { return false }
            guard depth < 128, visited.count < 20_000, let object = element as? NSObject,
                  visited.insert(ObjectIdentifier(object)).inserted else { return false }
            if visit(object) { return true }
            var children: [Any] = []
            if object.responds(to: NSSelectorFromString("accessibilityChildren")) { children = object.value(forKey: "accessibilityChildren") as? [Any] ?? [] }
            if let view = element as? NSView { children.append(contentsOf: view.subviews) }
            if let window = element as? NSWindow, let content = window.contentView { children.append(content) }
            for child in children where go(child, depth: depth + 1) { return true }
            return false
        }
        _ = go(root, depth: 0)
    }
    private static func identifier(of object: NSObject) -> String? {
        object.responds(to: NSSelectorFromString("accessibilityIdentifier")) ? object.value(forKey: "accessibilityIdentifier") as? String : nil
    }
    private static func node(_ element: Any, identifier wanted: String) -> NSObject? {
        var found: NSObject?
        walk(element) { object in
            guard identifier(of: object) == wanted else { return false }
            found = object
            return true
        }
        return found
    }
    /// Cards render as `Color.clear` once the camera leaves them behind, so
    /// this names what is actually drawn rather than what the layout holds. Read
    /// from the canvas's own cull pass (`MightyGraphInteractionProbe.drawnNodeIDs`):
    /// a card's `mighty-node-` element lives in the canvas's nested hosting view,
    /// which a CI runner (no assistive client) never exposes through accessibility.
    private static func mountedCards(_ window: NSWindow) -> Set<String> {
        window.contentView.flatMap { camera(in: $0) }?.drawnNodeIDs ?? []
    }
    /// The accessibility identifiers reachable under the canvas, for a failure report.
    private static func canvasIdentifiers(_ window: NSWindow) -> String {
        guard let host = window.contentView.flatMap({ camera(in: $0) })?.superview else { return "no canvas" }
        var ids: [String] = []
        walk(host) { object in
            if let id = identifier(of: object), !id.isEmpty { ids.append(id) }
            return ids.count >= 40
        }
        return "canvas ids=" + ids.joined(separator: ",")
    }
    private static func label(_ object: NSObject?) -> String {
        guard let object else { return "" }
        return ["accessibilityLabel", "accessibilityTitle", "accessibilityValue"].compactMap { key in
            object.responds(to: NSSelectorFromString(key)) ? object.value(forKey: key) as? String : nil
        }.joined(separator: " ")
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
