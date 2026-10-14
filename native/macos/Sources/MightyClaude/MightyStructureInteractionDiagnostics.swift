import AppKit
import MightyCore
import SwiftUI

/// The 구조 view's kanban board under real posted mouse and wheel events, in an isolated
/// fixture inside the pane dock's scroll view, as every pane is: it opens on the newest
/// request, ◀ shows the one before, the list jumps to any, a new request leaves an older
/// board where it is and shows the 새 요청 badge, whose click shows the newest board, and a
/// new request on the newest board is shown as it arrives, every column at its end. The
/// page scrolls sideways only, and each column on its own: a growing column at its end
/// follows it; drag-to-pan and wheel scrolling (`MightyStructurePanning`) move the column
/// under the pointer up and down and the page sideways, and nothing else; a drag on text
/// selects it; a click on a fold toggle opens it and stops that column's follow only; a
/// drag, a trackpad gesture with its momentum or a mouse wheel away from a column's end
/// stops that column's follow only and is never pulled back; another board opens at its
/// columns' tops, and the newest one again at their ends. Run from `--graph-smoke-test`.
///
/// SwiftUI's buttons have no native view and no accessibility element on a machine
/// with no assistive client (a CI runner), so the checks read the native scroll views,
/// the native text fields SwiftUI draws selectable text with, the frames the view
/// reports for its navigation controls, and the view's memory.
@MainActor
enum MightyStructureInteractionDiagnostics {
    private static let sessionID = "structure-fixture"
    /// A free board's columns.
    private static let columnIDs = ["request", "execution", "result"]

    static func run(store: AppStore) async -> [String: Any] {
        var report: [String: Any] = ["passed": false, "eventDelivery": "NSApp.postEvent"]
        let previousWindow = NSApp.keyWindow
        // Narrow enough that the board's three columns (280pt at least) scroll sideways.
        let window = NSWindow(contentRect: NSRect(x: 140, y: 120, width: 680, height: 600), styleMask: [.titled, .closable], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        window.title = "구조 끌기 검증"
        defer { window.orderOut(nil); window.close(); previousWindow?.makeKeyAndOrderFront(nil) }
        let memory = MightyStructureMemory()
        var probe: MightyStructurePanProbe?
        var scroll: NSScrollView?
        do {
            var runs = (0..<3).map { request($0, running: false, agents: 1) } + [request(3, running: true, agents: 10)]
            func boards() -> [MightyStructure.Board] { MightyStructure.boards(MightyStructure.make(runs: runs, style: nil, currentPhase: nil)) }
            func view() -> MightyStructureView {
                MightyStructureView(sessionID: sessionID, provider: "claude", boards: boards(),
                                    styleTitles: StyleRequestTitles(), newestRunID: runs.last?.id, memory: memory, onReveal: { _ in })
            }
            let host = NSHostingView(rootView: DockFixture(page: view()))
            window.contentView = host
            NSApp.activate(ignoringOtherApps: true)
            window.makeKeyAndOrderFront(nil)
            /// The page's origin and the origins it may take.
            func origin() -> CGPoint { scroll?.contentView.bounds.origin ?? .zero }
            func range() -> (minimum: CGPoint, maximum: CGPoint) {
                scroll.map { MightyStructurePanProbe.originRange($0.contentView) } ?? (.zero, .zero)
            }
            /// A column's native scroll view, its origin and the origins it may take.
            func columnScroll(_ id: String) -> NSScrollView? { memory.columnScroll(id) }
            func columnOrigin(_ id: String) -> CGPoint { columnScroll(id)?.contentView.bounds.origin ?? .zero }
            func columnRange(_ id: String) -> (minimum: CGPoint, maximum: CGPoint) {
                columnScroll(id).map { MightyStructurePanProbe.originRange($0.contentView) } ?? (.zero, .zero)
            }
            func columnHeight(_ id: String) -> CGFloat { columnScroll(id)?.documentView?.frame.height ?? 0 }
            /// Where a column's clip shows on the page, in the page scroll view's points.
            func area(_ id: String) -> CGRect {
                guard let scroll, let clip = columnScroll(id)?.contentView else { return .zero }
                return scroll.convert(clip.bounds, from: clip).intersection(scroll.contentView.frame)
            }
            func origins() -> [String: CGPoint] { Dictionary(uniqueKeysWithValues: columnIDs.map { ($0, columnOrigin($0)) }) }
            /// Every column but `moved` is where it was.
            func othersStill(_ before: [String: CGPoint], except moved: String?) -> Bool {
                columnIDs.allSatisfy { $0 == moved || !changed(columnOrigin($0), before[$0] ?? .zero) }
            }
            /// The column is at its end (its bottom in view) and follows it.
            func followsAtEnd(_ id: String) -> Bool {
                memory.columns.following(id) && abs(columnRange(id).maximum.y - columnOrigin(id).y) < 2
            }
            /// The board shown, by the view's own navigation.
            func shown() -> Int? { memory.navigation.index(in: boards()) }
            /// The requests whose texts the page draws: a request's own text and its
            /// sub-agents' outputs, each a native field.
            func drawnRequests() -> [Int] {
                guard let scroll else { return [] }
                return Array(Set(descendants(scroll, as: NSTextField.self).compactMap { field -> Int? in
                    guard let prefix = ["구조 요청 ", "출력 요청 "].first(where: field.stringValue.hasPrefix) else { return nil }
                    return field.stringValue.dropFirst(prefix.count).split(separator: " ").first.flatMap { Int($0) }
                })).sorted()
            }
            func columnsState() -> String {
                columnIDs.map { id in
                    let bounds = columnRange(id)
                    return "\(id){origin=\(NSStringFromPoint(columnOrigin(id))) min=\(NSStringFromPoint(bounds.minimum))"
                        + " max=\(NSStringFromPoint(bounds.maximum)) following=\(memory.columns.following(id))}"
                }.joined(separator: " ")
            }
            func state() -> String {
                let bounds = range()
                let hit: String = probe.flatMap { probe in scroll.map { probe.describe(probe.lastHitView, in: $0) } } ?? "nil"
                var parts = ["shown=\(shown().map(String.init) ?? "nil")/\(boards().count) chosen=\(memory.navigation.chosen ?? "nil") drawn=\(drawnRequests())"]
                parts.append("unseen=\(memory.navigation.hasUnseen(in: boards())) list=\(memory.listOpen) controls=\(memory.controlFrames.keys.sorted())")
                parts.append("page{origin=\(NSStringFromPoint(origin())) min=\(NSStringFromPoint(bounds.minimum)) max=\(NSStringFromPoint(bounds.maximum))"
                    + " document=\(NSStringFromSize(scroll?.documentView?.frame.size ?? .zero)) clip=\(NSStringFromSize(scroll?.contentView.bounds.size ?? .zero))}")
                parts.append(columnsState())
                parts.append("panning=\(probe?.isPanning.description ?? "nil") pans=\(probe?.pans ?? -1) replayed=\(probe?.replayedClicks ?? -1)")
                parts.append("target=\(probe?.lastTarget.map { "\($0)" } ?? "nil") column=\(probe?.lastColumn ?? "nil") pressHit=\(hit)")
                parts.append("expanded=\(memory.expanded.sorted()) key=\(window.isKeyWindow) active=\(NSApp.isActive)")
                parts.append("responder=\(window.firstResponder.map { String(describing: type(of: $0)) } ?? "nil")")
                return parts.joined(separator: " ")
            }
            /// A navigation control the view reported, in window points (bottom-up), and
            /// the point a click on it is posted at.
            ///
            /// SwiftUI's global space is not the hosting view's: on CI it counted from the
            /// top of the window's frame, title bar included, so a point taken as the
            /// hosting view's landed a title bar's height below the button. The page's
            /// scroll view reports its global frame too ("page"), and the pan probe is a
            /// native view with that very frame, so the probe's window frame maps the global
            /// space onto the window, whatever its origin. The point is then taken from
            /// AppKit's own hit test: the one nearest the control's middle whose press goes
            /// to the hosting view around the page, not into the page.
            func control(_ name: String) -> (point: CGPoint, frame: CGRect, detail: String)? {
                guard let global = memory.controlFrames[name], global.width > 0, global.height > 0,
                      let page = memory.controlFrames["page"], let probe, let scroll else { return nil }
                let probeWindow = probe.convert(probe.bounds, to: nil)
                let frame = CGRect(x: probeWindow.minX + global.minX - page.minX, y: probeWindow.maxY - (global.maxY - page.minY),
                                   width: global.width, height: global.height)
                let middle = CGPoint(x: frame.midX, y: frame.midY)
                var candidates: [CGPoint] = []
                var y = frame.minY + 2
                while y <= frame.maxY - 2 {
                    var x = frame.minX + 2
                    while x <= frame.maxX - 2 { candidates.append(CGPoint(x: x, y: y)); x += 2 }
                    y += 2
                }
                candidates.sort { hypot($0.x - middle.x, $0.y - middle.y) < hypot($1.x - middle.x, $1.y - middle.y) }
                let point = candidates.first { candidate in
                    guard let hit = probe.hitView(at: candidate), hit === host || hit.isDescendant(of: host) else { return false }
                    return probe.target(at: candidate, in: scroll) == .outside
                }
                let detail = "global=\(NSStringFromRect(global)) pageGlobal=\(NSStringFromRect(page)) probeWindow=\(NSStringFromRect(probeWindow))"
                    + " window=\(NSStringFromRect(frame)) point=\(point.map(NSStringFromPoint) ?? "none")"
                    + " hit=\(point.flatMap { probe.hitView(at: $0) }.map { String(describing: type(of: $0)) } ?? "none")"
                    + " content=\(NSStringFromRect(window.contentView?.frame ?? .zero))"
                return point.map { ($0, frame, detail) }
            }
            func controls() -> String {
                ["previous", "list", "next", "badge"].map { name in "\(name): " + (control(name)?.detail ?? "none") }.joined(separator: "; ")
            }
            /// A real click on a navigation control, outside the page's scroll view, built
            /// and posted as the fold toggle's click is (`post`: the same window number,
            /// event numbers, click count and pressure, `NSApp.postEvent` at the queue's
            /// end), after a move onto the control and with a moment between press and
            /// release. `landed` says whether the control ran.
            ///
            /// On CI such a click on the ◀ chip, mapped right onto it, did not run the
            /// SwiftUI button (d4909b9), while the fold toggle's click inside the page,
            /// which the pan probe holds and sends on, does. When the click does not land
            /// within `clickGrace`, the control's own action runs instead, the one its button
            /// calls, as the diagram smoke drives its SwiftUI buttons; the report says which
            /// drove it (`<name>DrivenBy`), and so does the step that checks the result.
            func press(_ name: String, landed: @escaping () -> Bool, action: () -> Void) async throws -> String {
                guard let target = control(name) else { throw MightyError("구조 보기의 \(name) 단추 자리를 모릅니다. " + controls() + " " + state()) }
                report[name + "Click"] = target.detail
                try post(window, .mouseMoved, at: target.point, number: 0)
                try await Task.sleep(for: .milliseconds(50))
                try post(window, .leftMouseDown, at: target.point, number: 1)
                try await Task.sleep(for: .milliseconds(60))
                try post(window, .leftMouseUp, at: target.point, number: 2)
                let deadline = ProcessInfo.processInfo.systemUptime + clickGrace
                while !landed(), ProcessInfo.processInfo.systemUptime < deadline { try await Task.sleep(for: .milliseconds(50)) }
                if landed() {
                    report[name + "DrivenBy"] = "real click"
                    return "a click on the \(name) button"
                }
                report[name + "DrivenBy"] = "\(name) action (same as the button): a posted click at \(NSStringFromPoint(target.point)) did not run it within \(clickGrace)s"
                action()
                return "the \(name) action (same as the button)"
            }
            try await store.waitForSmoke(timeout: 5, step: "structure fixture opens on the newest request, its page scrolls sideways only and its 실행 column follows its end",
                                         detail: state) {
                probe = descendants(host, as: MightyStructurePanProbe.self).first
                scroll = probe?.scrollView
                let bounds = range()
                let execution = columnRange("execution")
                return NSApp.isActive && window.isKeyWindow && scroll != nil && shown() == 3 && drawnRequests() == [3]
                    && bounds.maximum.x - bounds.minimum.x > 120 && bounds.maximum.y - bounds.minimum.y < 1
                    && execution.maximum.y - execution.minimum.y > 100 && memory.columns.following("execution")
                    && execution.maximum.y - columnOrigin("execution").y < 40
            }
            // The checks below speak in the scroll views' own top-down points.
            guard let probe, let scroll, scroll.isFlipped, columnScroll("execution")?.isFlipped == true else {
                throw MightyError("구조 보기의 위에서 아래로 재는 네이티브 스크롤 뷰를 찾지 못했습니다. " + state())
            }
            report["fixtureGeometry"] = ["origin": NSStringFromPoint(origin()), "maximum": NSStringFromPoint(range().maximum),
                                         "document": NSStringFromSize(scroll.documentView?.frame.size ?? .zero), "clip": NSStringFromSize(scroll.contentView.bounds.size),
                                         "columns": columnsState()]
            report["opensOnNewestBoard"] = true
            report["pageScrollsSidewaysOnly"] = true

            // 0a. A real click on ◀ shows the request before.
            try await store.waitForSmoke(timeout: 3, step: "the previous and list controls report their frames", detail: { controls() + " " + state() }) {
                control("previous") != nil && control("list") != nil && control("next") != nil && control("badge") == nil
            }
            let previousBy = try await press("previous", landed: { shown() != 3 }) { memory.navigation.step(-1, in: boards()) }
            try await store.waitForSmoke(timeout: 3, step: previousBy + " shows request 2 of 4",
                                         detail: { "click: \(report["previousClick"] ?? "") " + state() }) {
                shown() == 2 && memory.navigation.chosen == runs[2].id && drawnRequests() == [2]
            }
            report["previousShowsOlderBoard"] = true

            // 0b. A real click on the title opens the list; an entry's own action shows that board.
            let listBy = try await press("list", landed: { memory.listOpen }) { memory.listOpen.toggle() }
            try await store.waitForSmoke(timeout: 3, step: listBy + " opens the list of boards",
                                         detail: { "click: \(report["listClick"] ?? "") " + state() }) { memory.listOpen }
            report["listOpens"] = true
            // The entry's action, called as its button calls it: the list is a popover window
            // of its own, whose rows have no native view to click.
            memory.choose(boards()[0].id, in: boards())
            try await store.waitForSmoke(timeout: 3, step: "choosing the first list entry shows request 0 and closes the list", detail: state) {
                if !window.isKeyWindow { window.makeKeyAndOrderFront(nil) }
                return shown() == 0 && !memory.listOpen && drawnRequests() == [0] && window.isKeyWindow
            }
            report["listEntryJumps"] = "memory.choose (the list entry's action)"

            // 0c. A new request while an older board is shown: the view stays, and the badge shows.
            runs.append(request(4, running: true, agents: 10))
            host.rootView = DockFixture(page: view())
            try await store.waitForSmoke(timeout: 3, step: "a new request leaves the older board shown and shows the badge",
                                         detail: { controls() + " " + state() }) {
                boards().count == 5 && shown() == 0 && drawnRequests() == [0] && memory.navigation.hasUnseen(in: boards()) && control("badge") != nil
            }
            report["newRequestKeepsOlderBoard"] = true
            report["newerBadgeShows"] = true
            // 0d. A real click on the badge shows the newest board, which follows new ones again.
            let badgeBy = try await press("badge", landed: { shown() != 0 }) { memory.navigation.showNewest(in: boards()) }
            try await store.waitForSmoke(timeout: 3, step: badgeBy + " shows the newest request",
                                         detail: { "click: \(report["badgeClick"] ?? "") " + state() }) {
                shown() == 4 && memory.navigation.chosen == nil && drawnRequests() == [4] && control("badge") == nil
            }
            report["badgeShowsNewest"] = true

            // 0e. A new request while the newest board is shown: the view moves to it, and
            // every column shows its end and follows it.
            runs[4].status = "completed"
            runs.append(request(5, running: true, agents: 10))
            host.rootView = DockFixture(page: view())
            try await store.waitForSmoke(timeout: 3, step: "a new request on the newest board is shown as it arrives, every column at its end", detail: state) {
                boards().count == 6 && shown() == 5 && drawnRequests() == [5] && memory.navigation.chosen == nil && columnIDs.allSatisfy(followsAtEnd)
            }
            report["newRequestOnNewestMovesToIt"] = true
            report["boardsAfterNavigation"] = boards().count

            /// The page back at its first column, and column `id` at its end, following it again.
            func followAtEnd(_ id: String, _ step: String, detail: @escaping () -> String) async throws {
                try await store.waitForSmoke(timeout: 3, step: step, detail: detail) {
                    guard let column = columnScroll(id) else { return false }
                    if abs(origin().x - range().minimum.x) >= 1 {
                        scroll.contentView.scroll(to: CGPoint(x: range().minimum.x, y: origin().y))
                        scroll.reflectScrolledClipView(scroll.contentView)
                    }
                    let end = columnRange(id).maximum.y
                    guard memory.columns.following(id), abs(columnOrigin(id).y - end) < 1 else {
                        // Moving the column is a scroll: at its end it follows again.
                        column.contentView.scroll(to: CGPoint(x: columnOrigin(id).x, y: abs(columnOrigin(id).y - end) < 1 ? end - 40 : end))
                        column.reflectScrolledClipView(column.contentView)
                        return false
                    }
                    return abs(origin().x - range().minimum.x) < 1
                }
            }
            try await settle(store, scroll, step: "structure page settles on the newest board", detail: state)
            try await followAtEnd("execution", "the 실행 column back at its end follows it", detail: state)

            // 1. A column at its end follows it as it grows: three more sub-agents under 실행
            // keep its end in view, and neither the page nor another column moves.
            let growthPage = origin(), growthOthers = origins(), growthHeight = columnHeight("execution")
            runs[runs.count - 1].agents += late(20..<23, input: "따라가는 칸이 끝을 계속 보여 주는지 확인합니다.", output: "따라간 출력")
            host.rootView = DockFixture(page: view())
            try await store.waitForSmoke(timeout: 3, step: "the growing 실행 column at its end follows it",
                                         detail: { "before=\(growthHeight) " + state() }) {
                columnHeight("execution") > growthHeight + 10 && followsAtEnd("execution")
            }
            guard !changed(origin(), growthPage), othersStill(growthOthers, except: "execution") else {
                throw MightyError("끝을 따라가는 칸이 자라면서 페이지나 다른 칸을 움직였습니다. page=\(NSStringFromPoint(growthPage)) " + state())
            }
            report["growingColumnFollowsItsEnd"] = true

            // 2. While following, a drag down in the 실행 column moves it up, away from its
            // end: its follow stops, the other columns keep theirs, and new work does not
            // pull it back.
            try await settle(store, scroll, step: "structure page settles before the follow drag", detail: state)
            let followStart = origin(), followColumn = columnOrigin("execution"), followOthers = origins()
            let (down, downDetail) = try pagePoint(probe, scroll, in: area("execution"), room: CGSize(width: 0, height: 200))
            report["followDragPoint"] = downDetail
            try drag(window, scroll, from: down, to: CGPoint(x: down.x, y: down.y + 200))
            try await store.waitForSmoke(timeout: 3, step: "drag down in the following 실행 column moves it up",
                                         detail: { "start=\(NSStringFromPoint(followColumn)) chosen: \(downDetail) " + state() }) {
                !probe.isPanning && abs(columnOrigin("execution").y - (followColumn.y - 200)) <= 2
            }
            guard abs(origin().x - followStart.x) <= 2, !memory.columns.following("execution"), othersStill(followOthers, except: "execution"),
                  memory.columns.following("request"), memory.columns.following("result") else {
                throw MightyError("실행 칸 끌기가 그 칸의 따라가기만 멈추지 않았습니다. start=\(NSStringFromPoint(followColumn)) " + state())
            }
            report["dragPanStopsFollowing"] = true
            report["dragStopsOnlyItsColumn"] = true
            let readerColumn = columnOrigin("execution")
            let heightBefore = columnHeight("execution")
            // Four more sub-agents under 실행 make its column taller.
            runs[runs.count - 1].agents += late(10..<14, input: "위로 끌어 둔 화면을 당기지 않는지 확인합니다.", output: "늦게 온 출력")
            host.rootView = DockFixture(page: view())
            try await store.waitForSmoke(timeout: 3, step: "the 실행 column grows while the reader is up it",
                                         detail: { "before=\(heightBefore) " + state() }) { columnHeight("execution") > heightBefore + 10 }
            // Past the release grace and any follow pass the growth would have asked for.
            try await Task.sleep(for: .milliseconds(700))
            guard !changed(columnOrigin("execution"), readerColumn), !memory.columns.following("execution") else {
                throw MightyError("위로 끌어 둔 실행 칸을 새 내용이 다시 당겼습니다. reader=\(NSStringFromPoint(readerColumn)) " + state())
            }
            report["newContentDoesNotPullBack"] = true
            report["followGeometry"] = ["start": NSStringFromPoint(followColumn), "reader": NSStringFromPoint(readerColumn),
                                        "after": NSStringFromPoint(columnOrigin("execution")), "heightBefore": heightBefore,
                                        "heightAfter": columnHeight("execution")]

            // 3. A drag in a column moves the page sideways and the column up and down by
            // the drag: the grabbed point stays under the pointer, and no other column moves.
            try await settle(store, scroll, step: "structure page settles before the page drag", detail: state)
            let panStart = origin(), panColumn = columnOrigin("execution"), panOthers = origins()
            guard range().maximum.x - panStart.x >= 60, columnRange("execution").maximum.y - panColumn.y >= 50 else {
                throw MightyError("구조 끌기를 검증할 여유가 없습니다. " + state())
            }
            let (grab, grabDetail) = try pagePoint(probe, scroll, in: area("execution"), room: CGSize(width: -60, height: -50))
            report["pageDragPoint"] = grabDetail
            let pansBefore = probe.pans
            try drag(window, scroll, from: grab, to: CGPoint(x: grab.x - 60, y: grab.y - 50))
            try await store.waitForSmoke(timeout: 3, step: "a drag in the 실행 column moves the page sideways and the column up and down",
                                         detail: { "page=\(NSStringFromPoint(panStart)) column=\(NSStringFromPoint(panColumn)) chosen: \(grabDetail) " + state() }) {
                !probe.isPanning && probe.pans > pansBefore && abs(origin().x - panStart.x - 60) <= 2
                    && abs(columnOrigin("execution").y - panColumn.y - 50) <= 2
            }
            guard abs(origin().y - panStart.y) <= 2, othersStill(panOthers, except: "execution") else {
                throw MightyError("실행 칸 끌기가 다른 칸을 움직였습니다. " + state())
            }
            report["dragPanMovesPageSidewaysAndColumnVertically"] = true
            report["panGeometry"] = ["page": "\(NSStringFromPoint(panStart)) → \(NSStringFromPoint(origin()))",
                                     "column": "\(NSStringFromPoint(panColumn)) → \(NSStringFromPoint(columnOrigin("execution")))", "drag": "-60,-50"]

            // 4. A drag across selectable text selects it and leaves the page and the columns where they are.
            guard let field = selectableField(in: scroll, minimumWidth: 140) else { throw MightyError("선택할 수 있는 구조 본문이 화면에 없습니다. " + state()) }
            let fieldFrame = scroll.convert(field.bounds, from: field)
            let textStart = CGPoint(x: fieldFrame.minX + 4, y: fieldFrame.minY + 7)
            let textOrigin = origin(), textOthers = origins()
            let textPans = probe.pans
            try drag(window, scroll, from: textStart, to: CGPoint(x: textStart.x + 120, y: textStart.y))
            try await store.waitForSmoke(timeout: 3, step: "text drag selects text", detail: { "field=\(NSStringFromRect(fieldFrame)) " + state() }) {
                ((window.firstResponder as? NSTextView)?.selectedRange().length ?? 0) > 0
            }
            guard !changed(origin(), textOrigin), othersStill(textOthers, except: nil), probe.pans == textPans, !probe.isPanning,
                  probe.lastTarget == .textOrControl else {
                throw MightyError("구조 본문 끌기가 선택 대신 화면을 움직였습니다. before=\(NSStringFromPoint(textOrigin)) " + state())
            }
            report["textDragSelectsWithoutPanning"] = true
            report["selectedLength"] = (window.firstResponder as? NSTextView)?.selectedRange().length ?? 0

            // 5. A click on a fold toggle still opens its text: the press is held while it
            // might become a drag, then sent on to the button. The opened text stops its
            // column's follow, and only its column's.
            // Back to the page's first column and the 요청 column's top, where the request card and its toggle are.
            scroll.contentView.scroll(to: range().minimum)
            scroll.reflectScrolledClipView(scroll.contentView)
            if let requestColumn = columnScroll("request") {
                requestColumn.contentView.scroll(to: columnRange("request").minimum)
                requestColumn.reflectScrolledClipView(requestColumn.contentView)
            }
            try await settle(store, scroll, step: "structure page settles before the toggle click", detail: state)
            guard memory.columns.following("request"), memory.columns.following("result") else {
                throw MightyError("펼치기 전에 요청 칸과 결과 칸이 끝을 따라가지 않습니다. " + state())
            }
            guard let toggle = foldToggle(in: scroll, area: area("request"), memory: memory) else { throw MightyError("화면에 접힌 글의 펼치기 버튼이 없습니다. " + state()) }
            report["toggleClick"] = "key=\(toggle.key) point=\(NSStringFromPoint(toggle.point)) hit=\(probe.describe(probe.hitView(at: scroll.convert(toggle.point, to: nil)), in: scroll))"
            guard probe.target(at: scroll.convert(toggle.point, to: nil), in: scroll) == .page else {
                throw MightyError("펼치기 버튼 자리가 페이지로 판정되지 않습니다. \(report["toggleClick"] ?? "") " + state())
            }
            let clickOrigin = origin(), requestHeight = columnHeight("request"), requestOrigin = columnOrigin("request")
            let replayedBefore = probe.replayedClicks
            try click(window, scroll, at: toggle.point)
            try await store.waitForSmoke(timeout: 3, step: "click on a fold toggle opens the request and stops the 요청 column's follow only",
                                         detail: { "toggle=\(toggle.key) at=\(NSStringFromPoint(toggle.point)) " + state() }) {
                memory.expanded.contains(toggle.key) && !memory.columns.following("request") && columnHeight("request") > requestHeight + 10
            }
            // Past any follow pass the opened text's new size would have asked for: the
            // reader stays where the text begins, and the column still follows nothing.
            try await Task.sleep(for: .milliseconds(700))
            guard probe.replayedClicks > replayedBefore, !changed(origin(), clickOrigin), !changed(columnOrigin("request"), requestOrigin),
                  !memory.columns.following("request"), !probe.isPanning, memory.columns.following("result") else {
                throw MightyError("펼치기 클릭이 화면이나 요청 칸을 움직였거나, 그대로 전달되지 않았거나, 다른 칸의 따라가기를 멈췄습니다. "
                                  + "request=\(NSStringFromPoint(requestOrigin)) " + state())
            }
            report["foldToggleClickToggles"] = true
            report["foldStopsOnlyItsColumn"] = true

            // 6. A trackpad gesture with its momentum, and a mouse wheel, move the column
            // under the pointer up and down and the page sideways. The dock's scroll view
            // around the page took them all before.
            guard let dock = scroll.enclosingScrollView else { throw MightyError("구조 보기를 감싼 창 배치 스크롤 뷰가 없습니다. " + state()) }
            let dockStart = dock.contentView.bounds.origin
            func wheelState() -> String {
                let last = memory.lastReport.map { String(format: "{column=%@ originY=%.1f age=%.3fs userMoving=%@}", $0.column, $0.originY,
                                                          ProcessInfo.processInfo.systemUptime - $0.at, $0.userMoving.description) } ?? "nil"
                return "dock=\(NSStringFromPoint(dock.contentView.bounds.origin)) dockStart=\(NSStringFromPoint(dockStart))"
                    + " pageWheels=\(probe.pageWheels) columnWheels=\(probe.columnWheels) innerWheels=\(probe.innerWheels)"
                    + " moving=\(memory.movingColumn ?? "nil") reports=\(memory.reports) lastReport=\(last) " + state()
            }
            func dockStill() -> Bool { !changed(dock.contentView.bounds.origin, dockStart) }
            let wheelPans = probe.pans
            try await followAtEnd("execution", "the 실행 column back at its end follows it before the trackpad", detail: wheelState)
            try await settle(store, scroll, step: "structure page settles before the trackpad scroll", detail: wheelState)
            // 6a. Fingers moving down over the 실행 column's empty space, drifting a little
            // sideways, then momentum: the column shows 180pt more above and keeps to the
            // vertical axis; the page and the other columns stay.
            let trackpadPage = origin(), trackpadStart = columnOrigin("execution"), trackpadOthers = origins()
            let (space, spaceDetail) = try pagePoint(probe, scroll, in: area("execution"), room: .zero)
            let spacePoint = scroll.convert(space, to: nil)
            report["trackpadPoint"] = spaceDetail
            let columnWheelsBefore = probe.columnWheels
            report["trackpadWheel"] = try MightyGraphInteractionDiagnostics.postWheel(window: window, at: spacePoint, phase: .began)
            for _ in 0..<5 { try MightyGraphInteractionDiagnostics.postWheel(window: window, at: spacePoint, dx: -2, dy: 30, phase: .changed) }
            try MightyGraphInteractionDiagnostics.postWheel(window: window, at: spacePoint, phase: .ended)
            try MightyGraphInteractionDiagnostics.postWheel(window: window, at: spacePoint, dy: 10, momentum: .began)
            try MightyGraphInteractionDiagnostics.postWheel(window: window, at: spacePoint, dy: 10, momentum: .changed)
            try MightyGraphInteractionDiagnostics.postWheel(window: window, at: spacePoint, dy: 10, momentum: .changed)
            try MightyGraphInteractionDiagnostics.postWheel(window: window, at: spacePoint, momentum: .ended)
            try await store.waitForSmoke(timeout: 3, step: "trackpad scroll over the 실행 column's empty space moves that column up by its deltas",
                                         detail: { "start=\(NSStringFromPoint(trackpadStart)) expected=(+0,-180) chosen: \(spaceDetail) " + wheelState() }) {
                abs(columnOrigin("execution").y - (trackpadStart.y - 180)) <= 2 && !memory.columns.following("execution")
            }
            guard dockStill(), !changed(origin(), trackpadPage), othersStill(trackpadOthers, except: "execution"),
                  memory.columns.following("result"), probe.columnWheels > columnWheelsBefore, probe.pans == wheelPans else {
                throw MightyError("트랙패드 스크롤이 실행 칸만 움직이지 않았거나 창 배치를 움직였거나 끌기로 처리됐습니다. " + wheelState())
            }
            report["trackpadScrollMovesItsColumnVertically"] = true
            report["trackpadStopsOnlyItsColumn"] = true
            // 6b. Fingers moving left over a card's text, then momentum: the page shows 80pt
            // more to the right, and no column moves. Text keeps no wheel of its own.
            try await settle(store, scroll, step: "structure page settles before the sideways trackpad scroll", detail: wheelState)
            guard let wheelField = selectableField(in: scroll, area: area("execution"), minimumWidth: 140) else {
                throw MightyError("휠을 굴릴 구조 본문이 화면에 없습니다. " + wheelState())
            }
            let wheelFieldFrame = scroll.convert(wheelField.bounds, from: wheelField)
            let textPoint = scroll.convert(CGPoint(x: wheelFieldFrame.minX + 24, y: wheelFieldFrame.minY + 7), to: nil)
            report["wheelTextPoint"] = "field=\(NSStringFromRect(wheelFieldFrame)) hit=\(probe.describe(probe.hitView(at: textPoint), in: scroll))"
            let sidewaysStart = origin(), sidewaysOthers = origins()
            guard range().maximum.x - sidewaysStart.x >= 80 else { throw MightyError("옆으로 굴릴 여유가 없습니다. " + wheelState()) }
            try MightyGraphInteractionDiagnostics.postWheel(window: window, at: textPoint, phase: .began)
            for _ in 0..<4 { try MightyGraphInteractionDiagnostics.postWheel(window: window, at: textPoint, dx: -15, dy: 1, phase: .changed) }
            try MightyGraphInteractionDiagnostics.postWheel(window: window, at: textPoint, phase: .ended)
            try MightyGraphInteractionDiagnostics.postWheel(window: window, at: textPoint, dx: -10, momentum: .began)
            try MightyGraphInteractionDiagnostics.postWheel(window: window, at: textPoint, dx: -10, momentum: .changed)
            try MightyGraphInteractionDiagnostics.postWheel(window: window, at: textPoint, momentum: .ended)
            try await store.waitForSmoke(timeout: 3, step: "trackpad scroll over a card's text moves the page sideways by its deltas",
                                         detail: { "start=\(NSStringFromPoint(sidewaysStart)) expected=(+80,+0) " + wheelState() }) {
                abs(origin().x - (sidewaysStart.x + 80)) <= 2 && abs(origin().y - sidewaysStart.y) <= 2
            }
            guard dockStill(), othersStill(sidewaysOthers, except: nil) else {
                throw MightyError("옆으로 굴린 트랙패드가 창 배치나 칸을 움직였습니다. " + wheelState())
            }
            report["trackpadScrollMovesPageSideways"] = true
            // 6c. The 실행 column grows: past the wheel's grace nothing pulls the reader back.
            let wheelReader = columnOrigin("execution")
            let wheelHeight = columnHeight("execution")
            runs[runs.count - 1].agents += late(14..<17, input: "휠로 올려 둔 화면을 당기지 않는지 확인합니다.", output: "휠 뒤에 온 출력")
            host.rootView = DockFixture(page: view())
            try await store.waitForSmoke(timeout: 3, step: "the 실행 column grows after the trackpad scroll",
                                         detail: { "before=\(wheelHeight) " + wheelState() }) { columnHeight("execution") > wheelHeight + 10 }
            try await Task.sleep(for: .milliseconds(700))
            guard !changed(columnOrigin("execution"), wheelReader), !memory.columns.following("execution") else {
                throw MightyError("트랙패드로 옮긴 실행 칸이 700ms 안에 되돌아갔습니다. reader=\(NSStringFromPoint(wheelReader)) " + wheelState())
            }
            report["trackpadScrollNotPulledBack"] = true
            // 6d. A mouse wheel's steps, which count lines, over a card's text in the 실행
            // column, from its end while following: four steps up move the column, two to
            // the right the page, and the column stops following.
            try await followAtEnd("execution", "the 실행 column back at its end follows it before the mouse wheel", detail: wheelState)
            try await settle(store, scroll, step: "structure page settles before the mouse wheel", detail: wheelState)
            guard let stepField = selectableField(in: scroll, area: area("execution"), minimumWidth: 140),
                  let executionColumn = columnScroll("execution") else { throw MightyError("마우스 휠을 굴릴 구조 본문이 화면에 없습니다. " + wheelState()) }
            let stepFieldFrame = scroll.convert(stepField.bounds, from: stepField)
            let stepPoint = scroll.convert(CGPoint(x: stepFieldFrame.minX + 24, y: stepFieldFrame.minY + 7), to: nil)
            let stepPage = origin(), stepColumn = columnOrigin("execution"), stepOthers = origins()
            let lineY = executionColumn.verticalLineScroll, lineX = scroll.horizontalLineScroll
            let expected = CGPoint(x: stepPage.x + 4 * lineX, y: stepColumn.y - 8 * lineY)
            guard memory.columns.following("execution"), lineX > 0, lineY > 0, expected.x <= range().maximum.x,
                  expected.y >= columnRange("execution").minimum.y else {
                throw MightyError("마우스 휠을 굴릴 여유가 없습니다. expected=\(NSStringFromPoint(expected)) lines=(\(lineX),\(lineY)) " + wheelState())
            }
            report["mouseWheel"] = try MightyGraphInteractionDiagnostics.postWheel(window: window, at: stepPoint, dy: 2, continuous: false)
            for _ in 0..<3 { try MightyGraphInteractionDiagnostics.postWheel(window: window, at: stepPoint, dy: 2, continuous: false) }
            try MightyGraphInteractionDiagnostics.postWheel(window: window, at: stepPoint, dx: -2, continuous: false)
            try MightyGraphInteractionDiagnostics.postWheel(window: window, at: stepPoint, dx: -2, continuous: false)
            try await store.waitForSmoke(timeout: 3, step: "mouse wheel steps move the 실행 column and the page by their lines and stop the column's follow",
                                         detail: { "page=\(NSStringFromPoint(stepPage)) column=\(NSStringFromPoint(stepColumn)) expected=\(NSStringFromPoint(expected)) lines=(\(lineX),\(lineY)) " + wheelState() }) {
                abs(origin().x - expected.x) <= 2 && abs(columnOrigin("execution").y - expected.y) <= 2 && !memory.columns.following("execution")
            }
            let wheelEnd = columnOrigin("execution")
            try await Task.sleep(for: .milliseconds(700))
            guard dockStill(), !changed(columnOrigin("execution"), wheelEnd), !memory.columns.following("execution"),
                  othersStill(stepOthers, except: "execution") else {
                throw MightyError("마우스 휠로 옮긴 실행 칸이 되돌아갔거나 창 배치나 다른 칸이 움직였습니다. end=\(NSStringFromPoint(wheelEnd)) " + wheelState())
            }
            report["mouseWheelMovesColumnAndPage"] = true
            report["mouseWheelStopsFollowing"] = true
            // 6e. Fingers moving up over the opened request's text in the 요청 column: that
            // column shows 60pt more below, and the 실행 column stays where its reader left it.
            scroll.contentView.scroll(to: CGPoint(x: range().minimum.x, y: origin().y))
            scroll.reflectScrolledClipView(scroll.contentView)
            try await settle(store, scroll, step: "structure page settles before the 요청 column's trackpad scroll", detail: wheelState)
            let requestStart = columnOrigin("request"), requestOthers = origins(), requestPage = origin()
            guard columnRange("request").maximum.y - requestStart.y >= 60 else {
                throw MightyError("펼친 요청 칸을 굴릴 여유가 없습니다. " + wheelState())
            }
            guard let requestField = selectableField(in: scroll, area: area("request"), minimumWidth: 140) else {
                throw MightyError("굴릴 요청 칸의 본문이 화면에 없습니다. " + wheelState())
            }
            let requestFieldFrame = scroll.convert(requestField.bounds, from: requestField)
            let requestPoint = scroll.convert(CGPoint(x: requestFieldFrame.minX + 24, y: requestFieldFrame.minY + 7), to: nil)
            let requestDetail = "field=\(NSStringFromRect(requestFieldFrame)) hit=\(probe.describe(probe.hitView(at: requestPoint), in: scroll))"
            report["requestTrackpadPoint"] = requestDetail
            try MightyGraphInteractionDiagnostics.postWheel(window: window, at: requestPoint, phase: .began)
            for _ in 0..<3 { try MightyGraphInteractionDiagnostics.postWheel(window: window, at: requestPoint, dy: -20, phase: .changed) }
            try MightyGraphInteractionDiagnostics.postWheel(window: window, at: requestPoint, phase: .ended)
            try await store.waitForSmoke(timeout: 3, step: "trackpad scroll over the 요청 column moves that column only",
                                         detail: { "start=\(NSStringFromPoint(requestStart)) expected=(+0,+60) chosen: \(requestDetail) " + wheelState() }) {
                abs(columnOrigin("request").y - (requestStart.y + 60)) <= 2
            }
            guard dockStill(), !changed(origin(), requestPage), othersStill(requestOthers, except: "request") else {
                throw MightyError("요청 칸의 트랙패드 스크롤이 다른 칸이나 페이지를 움직였습니다. " + wheelState())
            }
            report["wheelMovesOnlyItsColumn"] = true
            report["wheelGeometry"] = ["trackpadStart": NSStringFromPoint(trackpadStart), "sidewaysStart": NSStringFromPoint(sidewaysStart),
                                       "wheelReader": NSStringFromPoint(wheelReader), "stepColumn": NSStringFromPoint(stepColumn),
                                       "stepEnd": NSStringFromPoint(wheelEnd), "lines": "\(lineX),\(lineY)", "requestStart": NSStringFromPoint(requestStart),
                                       "dock": NSStringFromPoint(dock.contentView.bounds.origin), "columnWheels": probe.columnWheels, "pageWheels": probe.pageWheels]

            // 7. Another board opens at its columns' tops and follows nothing; the newest
            // board again follows every column's end. The navigation's own actions, whose
            // buttons' clicks step 0 checks.
            memory.navigation.step(-1, in: boards())
            try await store.waitForSmoke(timeout: 3, step: "the board before opens with every column at its top, following nothing", detail: state) {
                shown() == 4 && drawnRequests() == [4] && columnRange("execution").maximum.y - columnRange("execution").minimum.y > 100
                    && columnIDs.allSatisfy { abs(columnOrigin($0).y - columnRange($0).minimum.y) < 1 && !memory.columns.following($0) }
                    && abs(origin().x - range().minimum.x) < 1
            }
            report["olderBoardOpensAtColumnTops"] = true
            memory.navigation.showNewest(in: boards())
            try await store.waitForSmoke(timeout: 3, step: "the newest board again shows every column's end and follows it", detail: state) {
                shown() == 5 && drawnRequests() == [5] && columnIDs.allSatisfy(followsAtEnd)
            }
            report["newestBoardFollowsEveryColumnAgain"] = true
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

    /// A free request long enough to fold, with a reply and `agents` sub-agents under it,
    /// every third one nested under the one before. With ten, the 실행 column is many
    /// times the window's height; the request, opened, is taller than its column; the
    /// three 280pt columns are wider than the window.
    private static func request(_ index: Int, running: Bool, agents: Int) -> MightyGraphRun {
        let input = (0..<24).map { "구조 요청 \(index) · 줄 \($0) · 끌어서 옮기는 구조 보기를 검증합니다." }.joined(separator: "\n")
        let reply = running ? "진행 중인 답 한 줄"
            : (0..<8).map { "답 \(index) · 줄 \($0) · 카드 본문은 선택할 수 있습니다." }.joined(separator: "\n")
        let blocks = (0..<agents).map { agent in
            MightyGraphAgent(id: "structure-\(index)-agent-\(agent)", parentID: agent % 3 == 2 ? "structure-\(index)-agent-\(agent - 1)" : nil,
                             title: "하위 에이전트 \(agent)", input: "요청 \(index)의 일을 나눠 맡습니다.",
                             status: running ? "running" : "completed",
                             entries: [LogEntry(id: "structure-\(index)-output-\(agent)", kind: "assistant",
                                                text: (0..<3).map { "출력 요청 \(index) · 하위 \(agent) · 줄 \($0) · 칸이 길어집니다." }.joined(separator: "\n"),
                                                provider: "claude")])
        }
        return MightyGraphRun(id: "structure-run-\(index)", input: input, status: running ? "running" : "completed",
                              rootEntries: [LogEntry(id: "structure-reply-\(index)", kind: "assistant", text: reply, provider: "claude")], agents: blocks)
    }

    /// Sub-agents that arrive late under the newest request, one output line each.
    private static func late(_ indices: Range<Int>, input: String, output: String) -> [MightyGraphAgent] {
        indices.map { index in
            MightyGraphAgent(id: "structure-late-\(index)", title: "늦은 하위 에이전트 \(index)", input: input, status: "running",
                             entries: [LogEntry(id: "structure-late-output-\(index)", kind: "assistant", text: "\(output) \(index)", provider: "claude")])
        }
    }

    /// A page point (scroll view coordinates) in `area`, a column's visible part, whose
    /// press would pan, with `room` for the drag inside that area, chosen from the native
    /// geometry: the point farthest from every text field, text view, control and inner
    /// scroll view (a gap between cards, a row's empty heading line, the column's
    /// padding), which AppKit's own hit test also sends to the page. `detail` says what
    /// was chosen.
    private static func pagePoint(_ probe: MightyStructurePanProbe, _ scroll: NSScrollView, in area: CGRect, room: CGSize) throws -> (point: CGPoint, detail: String) {
        let visible = area.insetBy(dx: 6, dy: 6)
        let busy = busyFrames(in: scroll, columns: probe.columns(in: scroll).map(\.scroll))
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
    /// instead of the page: native text, controls (the scroll bars too) and scroll views
    /// inside a card. The columns' own scroll views are the page's.
    private static func busyFrames(in scroll: NSScrollView, columns: [NSScrollView]) -> [CGRect] {
        var frames: [CGRect] = []
        func visit(_ view: NSView) {
            guard !view.isHiddenOrHasHiddenAncestor else { return }
            let inner = view is NSScrollView && view !== scroll && !columns.contains { $0 === view }
            if view is NSTextField || view is NSTextView || view is NSControl || inner {
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

    /// Waits until the page holds still: the page's and every column's origin and
    /// document size and every native text frame unchanged over a few polls, so a point
    /// chosen now is still empty when the press arrives.
    private static func settle(_ store: AppStore, _ scroll: NSScrollView, step: String, detail: @escaping () -> String) async throws {
        var last = "", same = 0
        try await store.waitForSmoke(timeout: 3, step: step, detail: { "polls=\(same) " + detail() }) {
            let scrolls = descendants(scroll, as: NSScrollView.self).map {
                NSStringFromPoint($0.contentView.bounds.origin) + NSStringFromSize($0.documentView?.frame.size ?? .zero)
            }
            let now = scrolls.joined() + descendants(scroll, as: NSTextField.self).map { NSStringFromRect(scroll.convert($0.bounds, from: $0)) }.joined()
            same = now == last ? same + 1 : 0
            last = now
            return same >= 4
        }
    }

    /// A selectable text SwiftUI draws as a native text field, whose first line is in
    /// view, in `area` (the page's visible part when nil) and in its column's.
    private static func selectableField(in scroll: NSScrollView, area: CGRect? = nil, minimumWidth: CGFloat) -> NSTextField? {
        let visible = (area ?? scroll.contentView.frame).insetBy(dx: 4, dy: 4)
        return descendants(scroll, as: NSTextField.self).first { field in
            let frame = scroll.convert(field.bounds, from: field)
            let points = [CGPoint(x: frame.minX + 4, y: frame.minY + 7), CGPoint(x: frame.minX + 124, y: frame.minY + 7)]
            return field.isSelectable && !field.isEditable && !field.isHiddenOrHasHiddenAncestor && frame.width >= minimumWidth
                && points.allSatisfy { visible.contains($0) && field.visibleRect.contains(field.convert($0, from: scroll)) }
        }
    }

    /// A folded request's or reply's 더 보기 button, right under its text, in view in
    /// `area`, its column's visible part. Both fold: a request has 24 lines over its 10, a
    /// reply's 8 lines wrap past its 8.
    private static func foldToggle(in scroll: NSScrollView, area: CGRect, memory: MightyStructureMemory) -> (point: CGPoint, key: String)? {
        let visible = area.insetBy(dx: 4, dy: 4)
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

    /// How long a posted click on a navigation control may take to run it.
    private static let clickGrace: TimeInterval = 1.5

    private static func post(_ window: NSWindow, _ type: NSEvent.EventType, at windowPoint: CGPoint, number: Int) throws {
        let press = type == .leftMouseDown || type == .leftMouseDragged
        guard let event = NSEvent.mouseEvent(with: type, location: windowPoint, modifierFlags: [], timestamp: ProcessInfo.processInfo.systemUptime,
                                             windowNumber: window.windowNumber, context: nil, eventNumber: number, clickCount: type == .mouseMoved ? 0 : 1,
                                             pressure: press ? 1 : 0) else { throw MightyError("구조 끌기 검증 이벤트를 만들지 못했습니다.") }
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
