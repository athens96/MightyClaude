import AppKit
import MightyCore
import SwiftUI

/// The 구조 view's drag-to-pan and wheel scrolling (`MightyStructurePanning`) under real
/// posted mouse and wheel events, in an isolated fixture inside the pane dock's scroll
/// view, as every pane is: a drag on the page moves it on both axes, a drag on text
/// selects it, a click on a fold toggle opens it, a drag away from the newest card stops
/// following, and a trackpad gesture with its momentum and a mouse wheel move the page
/// on both axes, stop following and are never pulled back. Run from `--graph-smoke-test`.
///
/// SwiftUI's buttons have no native view and no accessibility element on a machine
/// with no assistive client (a CI runner), so the checks read the native scroll view,
/// the native text fields SwiftUI draws selectable text with, and the view's memory.
@MainActor
enum MightyStructureInteractionDiagnostics {
    private static let sessionID = "structure-fixture"

    static func run(store: AppStore) async -> [String: Any] {
        var report: [String: Any] = ["passed": false, "eventDelivery": "NSApp.postEvent"]
        let previousWindow = NSApp.keyWindow
        let window = NSWindow(contentRect: NSRect(x: 140, y: 120, width: 760, height: 600), styleMask: [.titled, .closable], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        window.title = "구조 끌기 검증"
        defer { window.orderOut(nil); window.close(); previousWindow?.makeKeyAndOrderFront(nil) }
        let memory = MightyStructureMemory()
        var probe: MightyStructurePanProbe?
        var scroll: NSScrollView?
        do {
            var runs = fixtureRuns()
            func view() -> MightyStructureView {
                MightyStructureView(sessionID: sessionID, provider: "claude", shape: MightyStructure.make(runs: runs, style: nil, currentPhase: nil),
                                    styleTitles: StyleRequestTitles(), newestRunID: runs.last?.id, memory: memory, onReveal: { _ in })
            }
            let host = NSHostingView(rootView: DockFixture(page: view()))
            window.contentView = host
            NSApp.activate(ignoringOtherApps: true)
            window.makeKeyAndOrderFront(nil)
            func origin() -> CGPoint { scroll?.contentView.bounds.origin ?? .zero }
            func range() -> (minimum: CGPoint, maximum: CGPoint) {
                scroll.map { MightyStructurePanProbe.originRange($0.contentView) } ?? (.zero, .zero)
            }
            func state() -> String {
                let bounds = range()
                return "origin=\(NSStringFromPoint(origin())) min=\(NSStringFromPoint(bounds.minimum)) max=\(NSStringFromPoint(bounds.maximum))"
                    + " document=\(NSStringFromSize(scroll?.documentView?.frame.size ?? .zero)) clip=\(NSStringFromSize(scroll?.contentView.bounds.size ?? .zero))"
                    + " following=\(memory.follow.following) panning=\(probe?.isPanning.description ?? "nil") pans=\(probe?.pans ?? -1)"
                    + " replayed=\(probe?.replayedClicks ?? -1) target=\(probe?.lastTarget.map { "\($0)" } ?? "nil")"
                    + " pressHit=\(probe.flatMap { probe in scroll.map { probe.describe(probe.lastHitView, in: $0) } } ?? "nil")"
                    + " expanded=\(memory.expanded.sorted()) key=\(window.isKeyWindow) active=\(NSApp.isActive)"
                    + " responder=\(window.firstResponder.map { String(describing: type(of: $0)) } ?? "nil")"
            }
            try await store.waitForSmoke(timeout: 5, step: "structure fixture scrolls on both axes and follows the newest card", detail: state) {
                probe = descendants(host, as: MightyStructurePanProbe.self).first
                scroll = probe?.scrollView
                let bounds = range()
                return NSApp.isActive && window.isKeyWindow && scroll != nil
                    && bounds.maximum.x - bounds.minimum.x > 120 && bounds.maximum.y - bounds.minimum.y > 100
                    && memory.follow.following && bounds.maximum.y - origin().y < 40
            }
            // The checks below speak in the scroll view's own top-down points.
            guard let probe, let scroll, scroll.isFlipped else { throw MightyError("구조 보기의 위에서 아래로 재는 네이티브 스크롤 뷰를 찾지 못했습니다. " + state()) }
            report["fixtureGeometry"] = ["origin": NSStringFromPoint(origin()), "maximum": NSStringFromPoint(range().maximum),
                                         "document": NSStringFromSize(scroll.documentView?.frame.size ?? .zero), "clip": NSStringFromSize(scroll.contentView.bounds.size)]

            // 0. Finished requests mount folded and the running one open; a real click
            // on a folded card opens it and the page grows. Read from the view's memory
            // and the native text fields an open request draws, never accessibility.
            let finishedIDs = runs.filter { $0.status == "completed" }.map(\.id)
            let runningID = runs[runs.count - 1].id
            /// The requests drawn whole: each draws its text as a native field.
            func openRequests() -> [Int] {
                descendants(scroll, as: NSTextField.self).compactMap { field in
                    guard field.stringValue.hasPrefix("구조 요청 ") else { return nil }
                    return field.stringValue.dropFirst("구조 요청 ".count).split(separator: " ").first.flatMap { Int($0) }
                }.sorted()
            }
            func foldState() -> String { "folded=\(memory.foldedFrames.keys.sorted()) open=\(openRequests()) unfolded=\(memory.unfoldedCards.sorted()) " + state() }
            try await settle(store, scroll, step: "structure page settles with its finished requests folded", detail: foldState)
            try await store.waitForSmoke(timeout: 3, step: "finished requests mount folded, the running one open", detail: foldState) {
                !memory.foldedFrames.isEmpty && memory.foldedFrames.keys.allSatisfy(finishedIDs.contains)
                    && memory.foldedFrames[runningID] == nil && openRequests() == [runs.count - 1] && memory.unfoldedCards.isEmpty
            }
            report["finishedCardsMountFolded"] = true
            report["runningCardOpen"] = true
            report["mountedFolded"] = memory.foldedFrames.keys.sorted()
            let visibleClip = scroll.contentView.frame.insetBy(dx: 6, dy: 6)
            guard let (foldedID, foldedFrame) = memory.foldedFrames.filter({ visibleClip.intersects($0.value) }).max(by: { $0.key < $1.key }),
                  let foldedIndex = runs.firstIndex(where: { $0.id == foldedID }) else {
                throw MightyError("화면에 접힌 요청 카드가 없습니다. " + foldState())
            }
            let cardPoint = CGPoint(x: max(foldedFrame.minX, visibleClip.minX) + 24,
                                    y: min(max(foldedFrame.midY, visibleClip.minY + 4), visibleClip.maxY - 4))
            report["foldedCardClick"] = "id=\(foldedID) frame=\(NSStringFromRect(foldedFrame)) point=\(NSStringFromPoint(cardPoint))"
                + " hit=\(probe.describe(probe.hitView(at: scroll.convert(cardPoint, to: nil)), in: scroll))"
            guard foldedFrame.contains(cardPoint), probe.target(at: scroll.convert(cardPoint, to: nil), in: scroll) == .page else {
                throw MightyError("접힌 카드를 누를 자리가 없습니다. \(report["foldedCardClick"] ?? "") " + foldState())
            }
            let foldedDocument = scroll.documentView?.frame.height ?? 0
            let unfoldReplays = probe.replayedClicks
            try click(window, scroll, at: cardPoint)
            try await store.waitForSmoke(timeout: 3, step: "a click opens the folded request and the page grows",
                                         detail: { "id=\(foldedID) before=\(foldedDocument) " + foldState() }) {
                memory.unfoldedCards == [foldedID] && memory.foldedFrames[foldedID] == nil && openRequests().contains(foldedIndex)
                    && (scroll.documentView?.frame.height ?? 0) > foldedDocument + 40
            }
            guard probe.replayedClicks > unfoldReplays, probe.pans == 0 else { throw MightyError("접힌 카드 클릭이 끌기로 처리됐습니다. " + foldState()) }
            report["clickUnfoldsFoldedCard"] = true
            report["unfoldGrowth"] = ["before": foldedDocument, "after": scroll.documentView?.frame.height ?? 0]

            // The pan checks below read whole cards: every request opened, and the page
            // brought back to its end, where it follows the newest card again.
            memory.unfoldedCards = Set(finishedIDs)
            try await store.waitForSmoke(timeout: 3, step: "every request open", detail: foldState) {
                memory.foldedFrames.isEmpty && range().maximum.y - range().minimum.y > 600
            }
            try await settle(store, scroll, step: "structure page settles with every request open", detail: state)
            try await store.waitForSmoke(timeout: 3, step: "page back at its end follows the newest card", detail: state) {
                let end = range().maximum.y
                guard memory.follow.following, abs(origin().y - end) < 1 else {
                    // Moving the page is a scroll: at the end it follows again.
                    scroll.contentView.scroll(to: CGPoint(x: range().minimum.x, y: abs(origin().y - end) < 1 ? end - 40 : end))
                    scroll.reflectScrolledClipView(scroll.contentView)
                    return false
                }
                return true
            }

            // 1. While following, a drag down on the page moves it up, away from the
            // newest card: following stops, and new work does not pull the page back.
            try await settle(store, scroll, step: "structure page settles before the follow drag", detail: state)
            let followStart = origin()
            let (down, downDetail) = try pagePoint(probe, scroll, room: CGSize(width: 0, height: 200))
            report["followDragPoint"] = downDetail
            try drag(window, scroll, from: down, to: CGPoint(x: down.x, y: down.y + 200))
            try await store.waitForSmoke(timeout: 3, step: "drag down while following moves the page up",
                                         detail: { "start=\(NSStringFromPoint(followStart)) chosen: \(downDetail) " + state() }) {
                !probe.isPanning && abs(origin().y - (followStart.y - 200)) <= 2
            }
            guard abs(origin().x - followStart.x) <= 2, !memory.follow.following else {
                throw MightyError("따라가는 중의 구조 끌기가 따라가기를 멈추지 않았습니다. start=\(NSStringFromPoint(followStart)) " + state())
            }
            report["dragPanStopsFollowing"] = true
            let readerOrigin = origin()
            let documentBefore = scroll.documentView?.frame.height ?? 0
            // A free row is as tall as its tallest card, the folded request, so a longer
            // reply alone leaves the page as it was. Four sub-agents under 실행 stack in
            // its column and make the newest row, and the page, taller.
            runs[runs.count - 1].agents = (0..<4).map { index in
                MightyGraphAgent(id: "structure-late-\(index)", title: "늦은 하위 에이전트 \(index)", input: "위로 끌어 둔 화면을 당기지 않는지 확인합니다.",
                                 status: "running", entries: [LogEntry(id: "structure-late-output-\(index)", kind: "assistant",
                                                                       text: "늦게 온 출력 \(index) · 아래에서 자라도 읽던 자리는 그대로입니다.", provider: "claude")])
            }
            host.rootView = DockFixture(page: view())
            try await store.waitForSmoke(timeout: 3, step: "newest card grows while the reader is up the page",
                                         detail: { "before=\(documentBefore) " + state() }) { (scroll.documentView?.frame.height ?? 0) > documentBefore + 10 }
            // Past the release grace and any follow pass the growth would have asked for.
            try await Task.sleep(for: .milliseconds(700))
            guard abs(origin().x - readerOrigin.x) <= 2, abs(origin().y - readerOrigin.y) <= 2, !memory.follow.following else {
                throw MightyError("위로 끌어 둔 구조 보기를 새 내용이 다시 당겼습니다. reader=\(NSStringFromPoint(readerOrigin)) " + state())
            }
            report["newContentDoesNotPullBack"] = true
            report["followGeometry"] = ["start": NSStringFromPoint(followStart), "reader": NSStringFromPoint(readerOrigin),
                                        "after": NSStringFromPoint(origin()), "documentBefore": documentBefore,
                                        "documentAfter": scroll.documentView?.frame.height ?? 0]

            // 2. A drag on the page moves it on both axes by the drag: the grabbed
            // point stays under the pointer.
            try await settle(store, scroll, step: "structure page settles before the page drag", detail: state)
            let panStart = origin()
            let bounds = range()
            guard bounds.maximum.x - panStart.x >= 60, bounds.maximum.y - panStart.y >= 50 else {
                throw MightyError("구조 끌기를 검증할 여유가 없습니다. " + state())
            }
            let (grab, grabDetail) = try pagePoint(probe, scroll, room: CGSize(width: -60, height: -50))
            report["pageDragPoint"] = grabDetail
            let pansBefore = probe.pans
            try drag(window, scroll, from: grab, to: CGPoint(x: grab.x - 60, y: grab.y - 50))
            try await store.waitForSmoke(timeout: 3, step: "page drag moves the origin on both axes",
                                         detail: { "start=\(NSStringFromPoint(panStart)) chosen: \(grabDetail) " + state() }) {
                !probe.isPanning && probe.pans > pansBefore && abs(origin().x - panStart.x - 60) <= 2 && abs(origin().y - panStart.y - 50) <= 2
            }
            report["dragPanMovesOriginOnBothAxes"] = true
            report["panGeometry"] = ["start": NSStringFromPoint(panStart), "end": NSStringFromPoint(origin()), "drag": "-60,-50"]

            // 3. A drag across selectable text selects it and leaves the page where it is.
            guard let field = selectableField(in: scroll, minimumWidth: 140) else { throw MightyError("선택할 수 있는 구조 본문이 화면에 없습니다. " + state()) }
            let fieldFrame = scroll.convert(field.bounds, from: field)
            let textStart = CGPoint(x: fieldFrame.minX + 4, y: fieldFrame.minY + 7)
            let textOrigin = origin()
            let textPans = probe.pans
            try drag(window, scroll, from: textStart, to: CGPoint(x: textStart.x + 120, y: textStart.y))
            try await store.waitForSmoke(timeout: 3, step: "text drag selects text", detail: { "field=\(NSStringFromRect(fieldFrame)) " + state() }) {
                ((window.firstResponder as? NSTextView)?.selectedRange().length ?? 0) > 0
            }
            guard !changed(origin(), textOrigin), probe.pans == textPans, !probe.isPanning, probe.lastTarget == .textOrControl else {
                throw MightyError("구조 본문 끌기가 선택 대신 화면을 움직였습니다. before=\(NSStringFromPoint(textOrigin)) " + state())
            }
            report["textDragSelectsWithoutPanning"] = true
            report["selectedLength"] = (window.firstResponder as? NSTextView)?.selectedRange().length ?? 0

            // 4. A click on a fold toggle still opens its text: the press is held while
            // it might become a drag, then sent on to the button.
            // The page drag moved it right, past the first column's left edge: back to
            // the page's left edge, so whole cards and their buttons are in view.
            scroll.contentView.scroll(to: CGPoint(x: range().minimum.x, y: origin().y))
            scroll.reflectScrolledClipView(scroll.contentView)
            try await settle(store, scroll, step: "structure page settles before the toggle click", detail: state)
            guard let toggle = foldToggle(in: scroll, memory: memory) else { throw MightyError("화면에 접힌 글의 펼치기 버튼이 없습니다. " + state()) }
            report["toggleClick"] = "key=\(toggle.key) point=\(NSStringFromPoint(toggle.point)) hit=\(probe.describe(probe.hitView(at: scroll.convert(toggle.point, to: nil)), in: scroll))"
            guard probe.target(at: scroll.convert(toggle.point, to: nil), in: scroll) == .page else {
                throw MightyError("펼치기 버튼 자리가 페이지로 판정되지 않습니다. \(report["toggleClick"] ?? "") " + state())
            }
            let clickOrigin = origin()
            let replayedBefore = probe.replayedClicks
            try click(window, scroll, at: toggle.point)
            try await store.waitForSmoke(timeout: 3, step: "click on a fold toggle opens the request",
                                         detail: { "toggle=\(toggle.key) at=\(NSStringFromPoint(toggle.point)) " + state() }) {
                memory.expanded.contains(toggle.key)
            }
            guard probe.replayedClicks > replayedBefore, !changed(origin(), clickOrigin), !probe.isPanning else {
                throw MightyError("펼치기 클릭이 화면을 움직였거나 그대로 전달되지 않았습니다. " + state())
            }
            report["foldToggleClickToggles"] = true

            // 5. A trackpad gesture with its momentum, and a mouse wheel, move the page on
            // both axes. The dock's scroll view around the page took them all before.
            guard let dock = scroll.enclosingScrollView else { throw MightyError("구조 보기를 감싼 창 배치 스크롤 뷰가 없습니다. " + state()) }
            let dockStart = dock.contentView.bounds.origin
            func wheelState() -> String {
                "dock=\(NSStringFromPoint(dock.contentView.bounds.origin)) dockStart=\(NSStringFromPoint(dockStart))"
                    + " pageWheels=\(probe.pageWheels) innerWheels=\(probe.innerWheels) userMoving=\(memory.userMoving) " + state()
            }
            func dockStill() -> Bool { !changed(dock.contentView.bounds.origin, dockStart) }
            let wheelPans = probe.pans
            try await store.waitForSmoke(timeout: 3, step: "page back at its end follows the newest card before the wheel", detail: wheelState) {
                let end = range().maximum.y
                guard memory.follow.following, abs(origin().y - end) < 1, abs(origin().x - range().minimum.x) < 1 else {
                    scroll.contentView.scroll(to: CGPoint(x: range().minimum.x, y: abs(origin().y - end) < 1 ? end - 40 : end))
                    scroll.reflectScrolledClipView(scroll.contentView)
                    return false
                }
                return true
            }
            try await settle(store, scroll, step: "structure page settles before the trackpad scroll", detail: wheelState)
            // 5a. Fingers moving down over empty page space, drifting a little sideways, then
            // momentum: the page shows 180pt more above and keeps to the vertical axis.
            let trackpadStart = origin()
            let (space, spaceDetail) = try pagePoint(probe, scroll, room: .zero)
            let spacePoint = scroll.convert(space, to: nil)
            report["trackpadPoint"] = spaceDetail
            let pageWheelsBefore = probe.pageWheels
            report["trackpadWheel"] = try MightyGraphInteractionDiagnostics.postWheel(window: window, at: spacePoint, phase: .began)
            for _ in 0..<5 { try MightyGraphInteractionDiagnostics.postWheel(window: window, at: spacePoint, dx: -2, dy: 30, phase: .changed) }
            try MightyGraphInteractionDiagnostics.postWheel(window: window, at: spacePoint, phase: .ended)
            try MightyGraphInteractionDiagnostics.postWheel(window: window, at: spacePoint, dy: 10, momentum: .began)
            try MightyGraphInteractionDiagnostics.postWheel(window: window, at: spacePoint, dy: 10, momentum: .changed)
            try MightyGraphInteractionDiagnostics.postWheel(window: window, at: spacePoint, dy: 10, momentum: .changed)
            try MightyGraphInteractionDiagnostics.postWheel(window: window, at: spacePoint, momentum: .ended)
            try await store.waitForSmoke(timeout: 3, step: "trackpad scroll over empty page space moves the page up by its deltas",
                                         detail: { "start=\(NSStringFromPoint(trackpadStart)) expected=(+0,-180) chosen: \(spaceDetail) " + wheelState() }) {
                abs(origin().y - (trackpadStart.y - 180)) <= 2 && abs(origin().x - trackpadStart.x) <= 2 && !memory.follow.following
            }
            guard dockStill(), probe.pageWheels > pageWheelsBefore, probe.pans == wheelPans else {
                throw MightyError("트랙패드 스크롤이 구조 보기 대신 창 배치를 움직였거나 끌기로 처리됐습니다. " + wheelState())
            }
            report["trackpadScrollMovesPageVertically"] = true
            report["trackpadStopsFollowing"] = true
            // 5b. Fingers moving left over a card's text, then momentum: the page shows 80pt
            // more to the right. Text keeps no wheel of its own.
            try await settle(store, scroll, step: "structure page settles before the sideways trackpad scroll", detail: wheelState)
            guard let wheelField = selectableField(in: scroll, minimumWidth: 140) else { throw MightyError("휠을 굴릴 구조 본문이 화면에 없습니다. " + wheelState()) }
            let wheelFieldFrame = scroll.convert(wheelField.bounds, from: wheelField)
            let textPoint = scroll.convert(CGPoint(x: wheelFieldFrame.minX + 24, y: wheelFieldFrame.minY + 7), to: nil)
            report["wheelTextPoint"] = "field=\(NSStringFromRect(wheelFieldFrame)) hit=\(probe.describe(probe.hitView(at: textPoint), in: scroll))"
            let sidewaysStart = origin()
            guard range().maximum.x - sidewaysStart.x >= 80 else { throw MightyError("옆으로 굴릴 여유가 없습니다. " + wheelState()) }
            try MightyGraphInteractionDiagnostics.postWheel(window: window, at: textPoint, phase: .began)
            for _ in 0..<4 { try MightyGraphInteractionDiagnostics.postWheel(window: window, at: textPoint, dx: -15, dy: 1, phase: .changed) }
            try MightyGraphInteractionDiagnostics.postWheel(window: window, at: textPoint, phase: .ended)
            try MightyGraphInteractionDiagnostics.postWheel(window: window, at: textPoint, dx: -10, momentum: .began)
            try MightyGraphInteractionDiagnostics.postWheel(window: window, at: textPoint, dx: -10, momentum: .changed)
            try MightyGraphInteractionDiagnostics.postWheel(window: window, at: textPoint, momentum: .ended)
            try await store.waitForSmoke(timeout: 3, step: "trackpad scroll over a card's text moves the page sideways by its deltas",
                                         detail: { "start=\(NSStringFromPoint(sidewaysStart)) expected=(+80,+0) " + wheelState() }) {
                abs(origin().x - (sidewaysStart.x + 80)) <= 2 && abs(origin().y - sidewaysStart.y) <= 2 && !memory.follow.following
            }
            guard dockStill() else { throw MightyError("옆으로 굴린 트랙패드가 창 배치를 움직였습니다. " + wheelState()) }
            report["trackpadScrollMovesPageSideways"] = true
            // 5c. The newest card grows: past the wheel's grace nothing pulls the reader back.
            let wheelReader = origin()
            let wheelDocument = scroll.documentView?.frame.height ?? 0
            runs[runs.count - 1].agents += (4..<7).map { index in
                MightyGraphAgent(id: "structure-late-\(index)", title: "늦은 하위 에이전트 \(index)", input: "휠로 올려 둔 화면을 당기지 않는지 확인합니다.",
                                 status: "running", entries: [LogEntry(id: "structure-late-output-\(index)", kind: "assistant",
                                                                       text: "휠 뒤에 온 출력 \(index)", provider: "claude")])
            }
            host.rootView = DockFixture(page: view())
            try await store.waitForSmoke(timeout: 3, step: "newest card grows after the trackpad scroll",
                                         detail: { "before=\(wheelDocument) " + wheelState() }) { (scroll.documentView?.frame.height ?? 0) > wheelDocument + 10 }
            try await Task.sleep(for: .milliseconds(700))
            guard !changed(origin(), wheelReader), !memory.follow.following else {
                throw MightyError("트랙패드로 옮긴 구조 보기가 700ms 안에 되돌아갔습니다. reader=\(NSStringFromPoint(wheelReader)) " + wheelState())
            }
            report["trackpadScrollNotPulledBack"] = true
            // 5d. A mouse wheel's steps, which count lines, over a card's text: three steps
            // down and two to the left.
            try await settle(store, scroll, step: "structure page settles before the mouse wheel", detail: wheelState)
            guard let stepField = selectableField(in: scroll, minimumWidth: 140) else { throw MightyError("마우스 휠을 굴릴 구조 본문이 화면에 없습니다. " + wheelState()) }
            let stepFieldFrame = scroll.convert(stepField.bounds, from: stepField)
            let stepPoint = scroll.convert(CGPoint(x: stepFieldFrame.minX + 24, y: stepFieldFrame.minY + 7), to: nil)
            let stepStart = origin()
            let lineY = scroll.verticalLineScroll, lineX = scroll.horizontalLineScroll
            let expected = CGPoint(x: stepStart.x - 4 * lineX, y: stepStart.y + 6 * lineY)
            guard lineX > 0, lineY > 0, expected.x >= range().minimum.x, expected.y <= range().maximum.y else {
                throw MightyError("마우스 휠을 굴릴 여유가 없습니다. expected=\(NSStringFromPoint(expected)) lines=(\(lineX),\(lineY)) " + wheelState())
            }
            report["mouseWheel"] = try MightyGraphInteractionDiagnostics.postWheel(window: window, at: stepPoint, dy: -2, continuous: false)
            try MightyGraphInteractionDiagnostics.postWheel(window: window, at: stepPoint, dy: -2, continuous: false)
            try MightyGraphInteractionDiagnostics.postWheel(window: window, at: stepPoint, dy: -2, continuous: false)
            try MightyGraphInteractionDiagnostics.postWheel(window: window, at: stepPoint, dx: 2, continuous: false)
            try MightyGraphInteractionDiagnostics.postWheel(window: window, at: stepPoint, dx: 2, continuous: false)
            try await store.waitForSmoke(timeout: 3, step: "mouse wheel steps move the page by their lines on both axes",
                                         detail: { "start=\(NSStringFromPoint(stepStart)) expected=\(NSStringFromPoint(expected)) lines=(\(lineX),\(lineY)) " + wheelState() }) {
                abs(origin().x - expected.x) <= 2 && abs(origin().y - expected.y) <= 2
            }
            let wheelEnd = origin()
            try await Task.sleep(for: .milliseconds(700))
            guard dockStill(), !changed(origin(), wheelEnd), !memory.follow.following else {
                throw MightyError("마우스 휠로 옮긴 구조 보기가 되돌아갔거나 창 배치가 움직였습니다. end=\(NSStringFromPoint(wheelEnd)) " + wheelState())
            }
            report["mouseWheelMovesPageOnBothAxes"] = true
            report["wheelGeometry"] = ["trackpadStart": NSStringFromPoint(trackpadStart), "sidewaysStart": NSStringFromPoint(sidewaysStart),
                                       "wheelReader": NSStringFromPoint(wheelReader), "stepStart": NSStringFromPoint(stepStart),
                                       "stepEnd": NSStringFromPoint(wheelEnd), "lines": "\(lineX),\(lineY)",
                                       "dock": NSStringFromPoint(dock.contentView.bounds.origin), "pageWheels": probe.pageWheels]
            report["screenshot"] = try store.captureSmokeWindow(window, filename: "mighty-structure-pan.png").path
            report["passed"] = true
        } catch {
            report["error"] = error.localizedDescription
            if let url = try? store.captureSmokeWindow(window, filename: "mighty-structure-pan-failure.png") { report["failureScreenshot"] = url.path }
        }
        return report
    }

    /// The pane dock around every pane (`PaneDockView`): a two-axis scroll view whose
    /// content is its own size less the dock's inset. Left alone, it took the page's wheel.
    private struct DockFixture: View {
        let page: MightyStructureView
        var body: some View {
            GeometryReader { geometry in
                ScrollView([.horizontal, .vertical]) {
                    page.frame(width: max(DesignMetrics.Layout.paneMinWidth, geometry.size.width - 2 * DesignMetrics.Inset.dock),
                               height: max(DesignMetrics.Layout.paneMinHeight, geometry.size.height - 2 * DesignMetrics.Inset.dock))
                        .padding(DesignMetrics.Inset.dock)
                }
            }
        }
    }

    /// Eight free requests, each long enough to fold, with a reply under each: wider
    /// than the window (three 300pt columns) and many times its height.
    private static func fixtureRuns() -> [MightyGraphRun] {
        (0..<8).map { index in
            let input = (0..<14).map { "구조 요청 \(index) · 줄 \($0) · 끌어서 옮기는 구조 보기를 검증합니다." }.joined(separator: "\n")
            let reply = index == 7 ? "진행 중인 답 한 줄"
                : (0..<8).map { "답 \(index) · 줄 \($0) · 카드 본문은 선택할 수 있습니다." }.joined(separator: "\n")
            return MightyGraphRun(id: "structure-run-\(index)", input: input, status: index == 7 ? "running" : "completed",
                                  rootEntries: [LogEntry(id: "structure-reply-\(index)", kind: "assistant", text: reply, provider: "claude")])
        }
    }

    /// A page point (scroll view coordinates) whose press would pan, with `room` for
    /// the drag inside the visible page, chosen from the native geometry: the point
    /// farthest from every text field, text view, control and inner scroll view (a gap
    /// between cards or columns, a row's empty heading line, the page's padding), which
    /// AppKit's own hit test also sends to the page. `detail` says what was chosen.
    private static func pagePoint(_ probe: MightyStructurePanProbe, _ scroll: NSScrollView, room: CGSize) throws -> (point: CGPoint, detail: String) {
        let visible = scroll.contentView.frame.insetBy(dx: 6, dy: 6)
        let busy = busyFrames(in: scroll)
        var best: (point: CGPoint, clearance: CGFloat)?
        var y = visible.minY
        while y < visible.maxY {
            var x = visible.minX
            while x < visible.maxX {
                let point = CGPoint(x: x, y: y)
                if visible.contains(CGPoint(x: x + room.width, y: y + room.height)) {
                    let clearance = min(40, busy.map { distance(point, $0) }.min() ?? 40)
                    if clearance >= 10, clearance > (best?.clearance ?? 0), probe.target(at: scroll.convert(point, to: nil), in: scroll) == .page {
                        best = (point, clearance)
                    }
                }
                x += 10
            }
            y += 10
        }
        guard let best else {
            throw MightyError("구조 보기에서 끌 수 있는 빈 곳을 찾지 못했습니다. visible=\(NSStringFromRect(visible)) room=\(NSStringFromSize(room)) busy=\(busy.count)")
        }
        let hit = probe.hitView(at: scroll.convert(best.point, to: nil))
        return (best.point, "point=\(NSStringFromPoint(best.point)) clearance=\(best.clearance) hit=\(probe.describe(hit, in: scroll)) busy=\(busy.count)")
    }

    /// The frames, in the scroll view's points, of everything a press would go to
    /// instead of the page: native text, controls (the scroll bars too) and inner scroll views.
    private static func busyFrames(in scroll: NSScrollView) -> [CGRect] {
        var frames: [CGRect] = []
        func visit(_ view: NSView) {
            guard !view.isHiddenOrHasHiddenAncestor else { return }
            if view is NSTextField || view is NSTextView || view is NSControl || (view is NSScrollView && view !== scroll) {
                frames.append(scroll.convert(view.bounds, from: view)); return
            }
            view.subviews.forEach(visit)
        }
        visit(scroll)
        return frames
    }

    private static func distance(_ point: CGPoint, _ rect: CGRect) -> CGFloat {
        let dx = max(rect.minX - point.x, 0, point.x - rect.maxX), dy = max(rect.minY - point.y, 0, point.y - rect.maxY)
        return (dx * dx + dy * dy).squareRoot()
    }

    /// Waits until the page holds still: the origin, the document's size and every
    /// native text frame unchanged over a few polls, so a point chosen now is still
    /// empty when the press arrives.
    private static func settle(_ store: AppStore, _ scroll: NSScrollView, step: String, detail: @escaping () -> String) async throws {
        var last = "", same = 0
        try await store.waitForSmoke(timeout: 3, step: step, detail: { "polls=\(same) " + detail() }) {
            let now = NSStringFromPoint(scroll.contentView.bounds.origin) + NSStringFromSize(scroll.documentView?.frame.size ?? .zero)
                + busyFrames(in: scroll).map(NSStringFromRect).joined()
            same = now == last ? same + 1 : 0
            last = now
            return same >= 4
        }
    }

    /// A selectable text SwiftUI draws as a native text field, whose first line is in view.
    private static func selectableField(in scroll: NSScrollView, minimumWidth: CGFloat) -> NSTextField? {
        let visible = scroll.contentView.frame.insetBy(dx: 4, dy: 4)
        return descendants(scroll, as: NSTextField.self).first { field in
            let frame = scroll.convert(field.bounds, from: field)
            return field.isSelectable && !field.isEditable && !field.isHiddenOrHasHiddenAncestor && frame.width >= minimumWidth
                && visible.contains(CGPoint(x: frame.minX + 4, y: frame.minY + 7)) && visible.contains(CGPoint(x: frame.minX + 124, y: frame.minY + 7))
        }
    }

    /// A folded request's or reply's 더 보기 button, right under its text, in view. Both
    /// fold: a request has 14 lines over its 10, a reply's 8 lines wrap past its 8.
    private static func foldToggle(in scroll: NSScrollView, memory: MightyStructureMemory) -> (point: CGPoint, key: String)? {
        let visible = scroll.contentView.frame.insetBy(dx: 4, dy: 4)
        let texts = [("구조 요청 ", "request"), ("답 ", "message")]
        for field in descendants(scroll, as: NSTextField.self) {
            guard let (prefix, part) = texts.first(where: { field.stringValue.hasPrefix($0.0) }),
                  let index = field.stringValue.dropFirst(prefix.count).split(separator: " ").first.flatMap({ Int($0) }) else { continue }
            let key = MightyStructureView.foldKey(part, "structure-run-\(index)")
            guard !memory.expanded.contains(key) else { continue }
            // The SwiftUI host around the field holds the text's own frame.
            let frame = scroll.convert((field.superview ?? field).bounds, from: field.superview ?? field)
            let point = CGPoint(x: frame.minX + 10, y: frame.maxY + DesignMetrics.Spacing.xxs + 7)
            if visible.contains(point) { return (point, key) }
        }
        return nil
    }

    private static func post(_ window: NSWindow, _ type: NSEvent.EventType, at windowPoint: CGPoint, number: Int) throws {
        guard let event = NSEvent.mouseEvent(with: type, location: windowPoint, modifierFlags: [], timestamp: ProcessInfo.processInfo.systemUptime,
                                             windowNumber: window.windowNumber, context: nil, eventNumber: number, clickCount: 1,
                                             pressure: type == .leftMouseUp ? 0 : 1) else { throw MightyError("구조 끌기 검증 이벤트를 만들지 못했습니다.") }
        NSApp.postEvent(event, atStart: false)
    }
    /// A press, two drag steps and a release, in scroll view coordinates.
    private static func drag(_ window: NSWindow, _ scroll: NSScrollView, from start: CGPoint, to end: CGPoint) throws {
        let middle = CGPoint(x: (start.x + end.x) / 2, y: (start.y + end.y) / 2)
        for (index, item) in [(NSEvent.EventType.leftMouseDown, start), (.leftMouseDragged, middle), (.leftMouseDragged, end), (.leftMouseUp, end)].enumerated() {
            try post(window, item.0, at: scroll.convert(item.1, to: nil), number: index + 1)
        }
    }
    private static func click(_ window: NSWindow, _ scroll: NSScrollView, at point: CGPoint) throws {
        try post(window, .leftMouseDown, at: scroll.convert(point, to: nil), number: 1)
        try post(window, .leftMouseUp, at: scroll.convert(point, to: nil), number: 2)
    }
    private static func changed(_ a: CGPoint, _ b: CGPoint) -> Bool { abs(a.x - b.x) > 2 || abs(a.y - b.y) > 2 }
    private static func descendants<T: NSView>(_ view: NSView, as type: T.Type) -> [T] {
        ((view as? T).map { [$0] } ?? []) + view.subviews.flatMap { descendants($0, as: type) }
    }
}
