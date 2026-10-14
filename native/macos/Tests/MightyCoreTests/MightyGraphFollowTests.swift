import CoreGraphics
import Foundation
import Testing
@testable import MightyCore

/// The Mac diagram's follow mode: the newest block's bottom stays right above
/// the composer while the reader is there, and a reader who moved away is left
/// where they are.
struct MightyGraphFollowTests {
    private let viewport = CGSize(width: 900, height: 600)
    private func id(_ run: String, _ suffix: String) -> String { MightyGraphBlockSize.nodeID(runID: run, suffix: suffix) }
    private func layout(_ runs: [MightyGraphRun], draft: String = "", running: Bool = true) -> MightyGraphLayout {
        .make(runs: runs, draft: draft, running: running, expanded: [], splitsExecution: true)
    }
    private func frame(_ graph: MightyGraphLayout, _ nodeID: String?) -> CGRect? { graph.nodes.first { $0.id == nodeID }?.frame }
    /// Where the block's bottom is on screen under `camera`.
    private func bottom(_ frame: CGRect, _ camera: CGPoint, zoom: CGFloat = 1) -> CGFloat { camera.y + frame.maxY * zoom }

    @Test func typingADraftNeverChangesWhatTheCameraFollows() throws {
        // Symptom: pressing a key or Enter jumped the view. The draft block came and
        // went with the composer's text and was where the camera aimed.
        let runs = [MightyGraphRun(id: "a", status: "running")]
        let idle = layout(runs)
        let typing = layout(runs, draft: "다음 요청")
        #expect(typing.nodes.contains { $0.content == .draft })
        #expect(MightyGraphFollow.targetID(typing, runs: runs) == id("a", MightyGraphLayout.workSuffix))
        #expect(MightyGraphFollow.targetID(idle, runs: runs) == MightyGraphFollow.targetID(typing, runs: runs))
        // A finished pane always shows the draft block below its result; the result is followed.
        let done = [MightyGraphRun(id: "a", status: "completed")]
        #expect(MightyGraphFollow.targetID(layout(done, running: false), runs: done) == id("a", "result"))
        #expect(MightyGraphFollow.targetID(layout(done, draft: "x", running: false), runs: done) == id("a", "result"))
    }

    @Test func theFollowedBlockIsTheNewestRequestsLowestBlock() {
        // Symptom: a new sub-agent or result never moved the view.
        let agents = [MightyGraphAgent(id: "one", status: "running"), MightyGraphAgent(id: "deep", parentID: "one", status: "running")]
        let running = [MightyGraphRun(id: "old", status: "completed"), MightyGraphRun(id: "a", status: "running", agents: agents)]
        #expect(MightyGraphFollow.targetID(layout(running), runs: running) == id("a", "agent:deep"))
        var finished = running
        finished[1].status = "completed"
        finished[1].agents = finished[1].agents.map { var agent = $0; agent.status = "completed"; return agent }
        #expect(MightyGraphFollow.targetID(layout(finished, running: false), runs: finished) == id("a", "result"))
        // Siblings in one row: the one added last.
        let siblings = [MightyGraphRun(id: "a", status: "running", agents: [MightyGraphAgent(id: "left"), MightyGraphAgent(id: "right")])]
        #expect(MightyGraphFollow.targetID(layout(siblings), runs: siblings) == id("a", "agent:right"))
        // No request yet: the draft block is all there is.
        #expect(MightyGraphFollow.targetID(layout([], running: false), runs: []) == MightyGraphCamera.pendingNodeID)
    }

    @Test func aSendPutsTheNewRequestsBottomJustAboveTheComposer() throws {
        // Symptom: a sent request was put at the top of the canvas, its 실행 block
        // and everything after it far below.
        let runs = [MightyGraphRun(id: "old", status: "completed"), MightyGraphRun(id: "new", status: "running")]
        let graph = layout(runs)
        let target = try #require(MightyGraphFollow.targetID(graph, runs: runs))
        #expect(target == id("new", MightyGraphLayout.workSuffix))
        let work = try #require(frame(graph, target))
        for zoom in [CGFloat(1), 0.7] {
            let camera = MightyGraphFollow.cameraOffset(for: work, viewport: viewport, zoom: zoom, alignTop: false, alignBottom: false, follows: true)
            #expect(abs(bottom(work, camera, zoom: zoom) - (viewport.height - MightyGraphFollow.margin)) < 0.001)
            #expect(camera.x == MightyGraphFollow.centredX(viewport: viewport, zoom: zoom))
        }
        // Anything else keeps the shared rule.
        #expect(MightyGraphFollow.cameraOffset(for: work, viewport: viewport, zoom: 1, alignTop: true, alignBottom: false, follows: false)
                    == MightyGraphLayout.cameraOffset(for: work, viewport: viewport, zoom: 1, alignTop: true))
    }

    @Test func aFollowingCameraPutsEachNewBlockRightAboveTheComposer() throws {
        var watch = MightyGraphFollow.Watch()
        let work = CGRect(x: 24, y: 200, width: 500, height: 280)
        var camera = MightyGraphFollow.camera(bottomOf: work, viewport: viewport, zoom: 1, x: 50)
        watch.start()
        let step1 = watch.observe(targetID: "work", target: work, camera: camera, viewport: viewport, zoom: 1)
        #expect(step1 == nil)
        // A sub-agent appears below: its bottom goes where the 실행 block's was.
        let agent = CGRect(x: 94, y: 512, width: 360, height: 240)
        let movedPass = watch.observe(targetID: "agent", target: agent, camera: camera, viewport: viewport, zoom: 1)
        let moved = try #require(movedPass)
        #expect(bottom(agent, moved) == viewport.height - MightyGraphFollow.margin)
        // Only up and down: the reader's horizontal place is theirs.
        #expect(moved.x == camera.x)
        camera = moved
        let step2 = watch.observe(targetID: "agent", target: agent, camera: camera, viewport: viewport, zoom: 1)
        #expect(step2 == nil)
        #expect(watch.following)
    }

    @Test func aTallerComposerKeepsTheFollowedBlockAboveIt() throws {
        // Symptom: typing a second line or pressing Enter re-aimed the camera.
        var watch = MightyGraphFollow.Watch()
        let block = CGRect(x: 24, y: 900, width: 500, height: 280)
        let camera = MightyGraphFollow.camera(bottomOf: block, viewport: viewport, zoom: 1, x: 0)
        watch.start()
        _ = watch.observe(targetID: "b", target: block, camera: camera, viewport: viewport, zoom: 1)
        let shorter = CGSize(width: viewport.width, height: viewport.height - 40)
        let keptPass = watch.observe(targetID: "b", target: block, camera: camera, viewport: shorter, zoom: 1)
        let kept = try #require(keptPass)
        #expect(bottom(block, kept) == shorter.height - MightyGraphFollow.margin)
        #expect(kept.x == camera.x)
        // The composer shrinking back after the send gives the room back.
        let backPass = watch.observe(targetID: "b", target: block, camera: kept, viewport: viewport, zoom: 1)
        let back = try #require(backPass)
        #expect(back == camera)
    }

    @Test func aReaderWhoMovedAwayIsNeverPulledBack() {
        var watch = MightyGraphFollow.Watch()
        let block = CGRect(x: 24, y: 900, width: 500, height: 280)
        // Panned up: the block's bottom is 200pt below the composer.
        let up = CGPoint(x: 0, y: viewport.height - MightyGraphFollow.margin - block.maxY + 200)
        #expect(!MightyGraphFollow.isAtNewest(target: block, camera: up, viewport: viewport, zoom: 1))
        watch.moved(targetID: "b", target: block, camera: up, viewport: viewport, zoom: 1)
        #expect(!watch.following)
        let next = CGRect(x: 24, y: 1_212, width: 500, height: 200)
        let step3 = watch.observe(targetID: "c", target: next, camera: up, viewport: viewport, zoom: 1)
        #expect(step3 == nil)
        let step4 = watch.observe(targetID: "c", target: next, camera: up, viewport: CGSize(width: 900, height: 500), zoom: 1)
        #expect(step4 == nil)
        // Panned far past the newest block into empty canvas: not following either.
        let past = CGPoint(x: 0, y: -block.maxY - 50)
        #expect(!MightyGraphFollow.isAtNewest(target: block, camera: past, viewport: viewport, zoom: 1))
    }

    @Test func aReaderWhoScrolledUpBeforeALayoutLandedIsNotFollowedThroughIt() {
        // The CI smoke's sequence: the reader scrolls up 120pt while the layout of a
        // trim is still on its way, so the move is judged on the frames they saw. The
        // trim then moves every block up by thousands of points, and a new block
        // appears; neither may take the camera back to the newest block.
        var watch = MightyGraphFollow.Watch()
        let block = CGRect(x: 24, y: 30_000, width: 360, height: 240)
        let aligned = MightyGraphFollow.camera(bottomOf: block, viewport: CGSize(width: 1_020, height: 611), zoom: 1, x: 236)
        watch.start()
        let settled = watch.observe(targetID: "b", target: block, camera: aligned, viewport: CGSize(width: 1_020, height: 611), zoom: 1)
        #expect(settled == nil)
        let up = CGPoint(x: aligned.x, y: aligned.y + 120)
        watch.moved(targetID: "b", target: block, camera: up, viewport: CGSize(width: 1_020, height: 611), zoom: 1)
        #expect(!watch.following)
        let trimmed = block.offsetBy(dx: 0, dy: -10_112)
        let afterTrim = watch.observe(targetID: "b", target: trimmed, camera: up, viewport: CGSize(width: 1_020, height: 611), zoom: 1)
        #expect(afterTrim == nil)
        let late = CGRect(x: 640, y: trimmed.minY, width: 360, height: 240)
        let afterBlock = watch.observe(targetID: "late", target: late, camera: up, viewport: CGSize(width: 1_020, height: 611), zoom: 1)
        #expect(afterBlock == nil)
        #expect(!watch.following)
    }

    @Test func aReaderWhoComesBackToTheNewestBlockFollowsAgain() throws {
        var watch = MightyGraphFollow.Watch()
        let block = CGRect(x: 24, y: 900, width: 500, height: 280)
        watch.stop()
        #expect(!watch.following)
        // Within the slack below the composer still counts as there.
        let near = CGPoint(x: 0, y: viewport.height - MightyGraphFollow.margin - block.maxY + MightyGraphFollow.slack - 1)
        watch.moved(targetID: "b", target: block, camera: near, viewport: viewport, zoom: 1)
        #expect(watch.following)
        // The block stays where the reader left it until a new one arrives.
        let step5 = watch.observe(targetID: "b", target: block, camera: near, viewport: viewport, zoom: 1)
        #expect(step5 == nil)
        let next = CGRect(x: 24, y: 1_212, width: 500, height: 200)
        let movedPass = watch.observe(targetID: "c", target: next, camera: near, viewport: viewport, zoom: 1)
        let moved = try #require(movedPass)
        #expect(bottom(next, moved) == viewport.height - MightyGraphFollow.margin)
    }

    @Test func aReaderBelowTheNewestBlockKeepsTheirPlaceWhileItSettles() throws {
        var watch = MightyGraphFollow.Watch()
        let result = CGRect(x: 24, y: 900, width: 500, height: 400)
        // The reader panned down: the result's bottom is mid-screen, over the draft block.
        let camera = CGPoint(x: 0, y: 300 - result.maxY)
        watch.moved(targetID: "r", target: result, camera: camera, viewport: viewport, zoom: 1)
        #expect(watch.following)
        // Measured, the result is shorter: its bottom keeps its place on screen.
        let measured = CGRect(x: 24, y: 900, width: 500, height: 250)
        let keptPass = watch.observe(targetID: "r", target: measured, camera: camera, viewport: viewport, zoom: 1)
        let kept = try #require(keptPass)
        #expect(bottom(measured, kept) == 300)
    }

    @Test func aTrimAboveKeepsTheFollowedBlockWhereItWas() throws {
        var watch = MightyGraphFollow.Watch()
        let block = CGRect(x: 24, y: 9_000, width: 500, height: 280)
        let camera = MightyGraphFollow.camera(bottomOf: block, viewport: viewport, zoom: 0.8, x: 10)
        watch.start()
        _ = watch.observe(targetID: "b", target: block, camera: camera, viewport: viewport, zoom: 0.8)
        let trimmed = block.offsetBy(dx: 0, dy: -6_000)
        let keptPass = watch.observe(targetID: "b", target: trimmed, camera: camera, viewport: viewport, zoom: 0.8)
        let kept = try #require(keptPass)
        #expect(abs(bottom(trimmed, kept, zoom: 0.8) - (viewport.height - MightyGraphFollow.margin)) < 0.001)
    }

    @Test func openingOrResizingABlockNeverMovesTheCameraAndKeepsFollowOnlyAtTheNewest() {
        var watch = MightyGraphFollow.Watch()
        let block = CGRect(x: 24, y: 900, width: 500, height: 280)
        let camera = MightyGraphFollow.camera(bottomOf: block, viewport: viewport, zoom: 1, x: 0)
        watch.start()
        let settle = watch.observe(targetID: "b", target: block, camera: camera, viewport: viewport, zoom: 1)
        #expect(settle == nil)
        // Opened, the block is taller; its bottom is still in view: nothing moves, still following.
        watch.userChangedLayout()
        let opened = CGRect(x: 24, y: 900, width: 500, height: 300)
        let first = watch.observe(targetID: "b", target: opened, camera: camera, viewport: viewport, zoom: 1)
        #expect(first == nil)
        #expect(watch.following)
        // Opened far past the composer: nothing moves, and following stops.
        watch.userChangedLayout()
        let tall = CGRect(x: 24, y: 900, width: 500, height: 700)
        let second = watch.observe(targetID: "b", target: tall, camera: camera, viewport: viewport, zoom: 1)
        #expect(second == nil)
        #expect(!watch.following)
    }

    @Test func aBlockTallerThanTheViewportShowsItsBottom() {
        let tall = CGRect(x: 24, y: 100, width: 500, height: 2_000)
        let camera = MightyGraphFollow.camera(bottomOf: tall, viewport: viewport, zoom: 1, x: 0)
        #expect(bottom(tall, camera) == viewport.height - MightyGraphFollow.margin)
        #expect(MightyGraphFollow.isAtNewest(target: tall, camera: camera, viewport: viewport, zoom: 1))
        // Scrolled up inside it, past the slack: the reader is reading it.
        let reading = CGPoint(x: 0, y: camera.y + 400)
        #expect(!MightyGraphFollow.isAtNewest(target: tall, camera: reading, viewport: viewport, zoom: 1))
    }

    @Test func anAimElsewhereStopsFollowingUntilTheNextSend() throws {
        var watch = MightyGraphFollow.Watch()
        watch.stop()
        let block = CGRect(x: 24, y: 900, width: 500, height: 280)
        let step6 = watch.observe(targetID: "b", target: block, camera: .zero, viewport: viewport, zoom: 1)
        #expect(step6 == nil)
        watch.start()
        let placedPass = watch.observe(targetID: "b", target: block, camera: .zero, viewport: viewport, zoom: 1)
        let placed = try #require(placedPass)
        #expect(bottom(block, placed) == viewport.height - MightyGraphFollow.margin)
        #expect(placed.x == 0)
    }
}

/// The 구조 view follows the newest card the same way, in a scrolling page.
struct MightyStructureFollowTests {
    private typealias Page = MightyGraphFollow.Page
    private func page(origin: Double, visible: Double = 600, document: Double, newest: Double?, id: String? = "b") -> Page {
        Page(originY: origin, visibleHeight: visible, documentHeight: document, newestID: id, newestBottom: newest)
    }

    @Test func aReaderAtTheTrueBottomIsNeverPulledUpToTheNewestCard() {
        // Symptom: the structure would not stay scrolled to its bottom. The newest card
        // sits in a shorter column, 300pt above the end of the page.
        var watch = MightyGraphFollow.PageWatch()
        _ = watch.observe(page(origin: 2_000, document: 3_000, newest: 2_500), userScrolling: false)
        // The reader scrolls all the way down.
        let step7 = watch.observe(page(origin: 2_400, document: 3_000, newest: 2_700), userScrolling: false)
        #expect(step7 == nil)
        #expect(watch.following)
        // The newest card grows while staying above the viewport's bottom: nothing moves.
        let step8 = watch.observe(page(origin: 2_400, document: 3_000, newest: 2_760), userScrolling: false)
        #expect(step8 == nil)
        let step9 = watch.observe(page(origin: 2_400, document: 3_000, newest: 2_960), userScrolling: false)
        #expect(step9 == nil)
    }

    @Test func aGrowthThatHidesTheNewestCardIsFollowedInOnePass() {
        // Symptom: a card that grew by more than the slack in one layout pass was not
        // followed, because the follow flag was read from the grown page.
        var watch = MightyGraphFollow.PageWatch()
        _ = watch.observe(page(origin: 1_400, document: 2_000, newest: 1_992), userScrolling: false)
        #expect(watch.following)
        let step10 = watch.observe(page(origin: 1_400, document: 2_300, newest: 2_292), userScrolling: false)
        #expect(step10 == .bottom)
        #expect(watch.following)
        // The scroll lands at the bottom and is still following.
        let step11 = watch.observe(page(origin: 1_700, document: 2_300, newest: 2_292), userScrolling: false)
        #expect(step11 == nil)
        #expect(watch.following)
    }

    @Test func aCardInAShorterColumnIsShownByItsOwnBottom() {
        var watch = MightyGraphFollow.PageWatch()
        _ = watch.observe(page(origin: 1_000, document: 3_000, newest: 1_590), userScrolling: false)
        #expect(watch.following)
        let step12 = watch.observe(page(origin: 1_000, document: 3_000, newest: 1_700), userScrolling: false)
        #expect(step12 == .card("b"))
    }

    @Test func aReaderWhoScrolledUpStaysWhereTheyAre() {
        var watch = MightyGraphFollow.PageWatch()
        _ = watch.observe(page(origin: 1_400, document: 2_000, newest: 1_992), userScrolling: false)
        let step13 = watch.observe(page(origin: 300, document: 2_000, newest: 1_992), userScrolling: false)
        #expect(step13 == nil)
        #expect(!watch.following)
        let step14 = watch.observe(page(origin: 300, document: 2_400, newest: 2_392), userScrolling: false)
        #expect(step14 == nil)
        let step15 = watch.observe(page(origin: 300, visible: 560, document: 2_400, newest: 2_392), userScrolling: false)
        #expect(step15 == nil)
        // Back at the newest card: following again.
        let step16 = watch.observe(page(origin: 1_800, visible: 560, document: 2_400, newest: 2_392), userScrolling: false)
        #expect(step16 == nil)
        #expect(watch.following)
    }

    @Test func aScrollInProgressIsNeverFought() {
        var watch = MightyGraphFollow.PageWatch()
        _ = watch.observe(page(origin: 1_400, document: 2_000, newest: 1_992), userScrolling: false)
        // The page grows while the user's fingers are moving it up.
        let step17 = watch.observe(page(origin: 1_200, document: 2_300, newest: 2_292), userScrolling: true)
        #expect(step17 == nil)
        #expect(!watch.following)
    }

    @Test func aUsersMoveIsJudgedWhenItHappensNotWhenItsReportArrives() {
        // Symptom (CI, 구조 trackpad smoke): the page moved 180pt up from the newest card,
        // yet kept following. Its report arrived late, after the move's grace, together
        // with a layout change, and was judged as layout.
        var watch = MightyGraphFollow.PageWatch()
        _ = watch.observe(page(origin: 1_523, visible: 577, document: 2_100, newest: 2_091), userScrolling: false)
        #expect(watch.following)
        var blind = watch
        // The late report: moved and laid out at once, no move known. Following stays on,
        // and the reader is pulled back to the newest card.
        #expect(blind.observe(page(origin: 1_343, visible: 577, document: 2_130, newest: 2_121), userScrolling: false) == .bottom)
        #expect(blind.following)
        // The probe tells the rule the moment it moves the page.
        watch.userScrolled(byY: -180)
        #expect(!watch.following)
        // The same late report then keeps the reader where they are.
        #expect(watch.observe(page(origin: 1_343, visible: 577, document: 2_130, newest: 2_121), userScrolling: false) == nil)
        #expect(!watch.following)
    }

    @Test func aUsersMoveNearTheNewestCardKeepsFollowing() {
        var watch = MightyGraphFollow.PageWatch()
        _ = watch.observe(page(origin: 1_400, document: 2_000, newest: 1_992), userScrolling: false)
        // 20pt up: the newest card's bottom is hidden by less than the slack.
        watch.userScrolled(byY: -20)
        #expect(watch.following)
        // A sideways move keeps the state it had.
        watch.userScrolled(byY: 0)
        #expect(watch.following)
        // Its report is judged as the scroll it is: still at the newest card, nothing moves.
        // A growth after it is followed.
        #expect(watch.observe(page(origin: 1_380, document: 2_000, newest: 1_992), userScrolling: false) == nil)
        #expect(watch.observe(page(origin: 1_380, document: 2_300, newest: 2_292), userScrolling: false) == .bottom)
        #expect(watch.following)
    }

    @Test func aUsersMoveBeforeAnyReportJudgesNothing() {
        var watch = MightyGraphFollow.PageWatch()
        watch.userScrolled(byY: -300)
        #expect(watch.following)
        // Back from a user move, a later return to the newest card follows again.
        _ = watch.observe(page(origin: 1_400, document: 2_000, newest: 1_992), userScrolling: false)
        watch.userScrolled(byY: -300)
        #expect(!watch.following)
        watch.userScrolled(byY: 300)
        #expect(watch.following)
    }

    @Test func aTallerComposerKeepsTheNewestCardAboveIt() {
        var watch = MightyGraphFollow.PageWatch()
        _ = watch.observe(page(origin: 1_400, document: 2_000, newest: 1_992), userScrolling: false)
        let step18 = watch.observe(page(origin: 1_400, visible: 560, document: 2_000, newest: 1_992), userScrolling: false)
        #expect(step18 == .bottom)
    }

    @Test func aNewRequestIsShownAndFollowedFromThen() {
        var watch = MightyGraphFollow.PageWatch()
        _ = watch.observe(page(origin: 1_400, document: 2_000, newest: 1_992), userScrolling: false)
        _ = watch.observe(page(origin: 200, document: 2_000, newest: 1_992), userScrolling: false)
        #expect(!watch.following)
        let step19 = watch.newRequest("c")
        #expect(step19 == .card("c"))
        #expect(watch.following)
        // The lazy page lands short of it: still following, and shown again.
        let step20 = watch.observe(page(origin: 1_500, document: 2_400, newest: 2_392, id: "c"), userScrolling: false)
        #expect(step20 == .bottom)
        let step21 = watch.observe(page(origin: 1_800, document: 2_400, newest: 2_392, id: "c"), userScrolling: false)
        #expect(step21 == nil)
        #expect(watch.following)
    }

    @Test func openingATextStopsFollowing() {
        var watch = MightyGraphFollow.PageWatch()
        _ = watch.observe(page(origin: 1_400, document: 2_000, newest: 1_992), userScrolling: false)
        watch.stop()
        #expect(!watch.following)
        let step22 = watch.observe(page(origin: 1_400, document: 2_300, newest: 2_292), userScrolling: false)
        #expect(step22 == nil)
    }

    @Test func aPageReopenedAtItsNewestCardFollowsAgain() {
        var watch = MightyGraphFollow.PageWatch()
        watch.stop()
        let opened = watch.appeared(newestID: "b")
        #expect(opened == nil)
        // The page opens at its end, where the newest card is.
        let first = watch.observe(page(origin: 1_400, document: 2_000, newest: 1_992), userScrolling: false)
        #expect(first == nil)
        #expect(watch.following)
    }

    @Test func comingBackShowsTheNewestCardOnlyToAFollowingReader() {
        var watch = MightyGraphFollow.PageWatch()
        let step23 = watch.appeared(newestID: "b")
        #expect(step23 == .card("b"))
        watch.stop()
        let step24 = watch.appeared(newestID: "b")
        #expect(step24 == nil)
        // An empty pane follows its page's end.
        var empty = MightyGraphFollow.PageWatch()
        let step25 = empty.observe(page(origin: 0, document: 40, newest: nil, id: nil), userScrolling: false)
        #expect(step25 == nil)
        #expect(empty.following)
    }
}

/// The 보드 view's columns each scroll on their own and follow their own end.
struct MightyStructureColumnFollowTests {
    private typealias Page = MightyGraphFollow.Page
    /// A column 600pt tall whose last card ends 8pt above its document's end.
    private func page(_ column: String, origin: Double, document: Double) -> Page {
        Page(originY: origin, visibleHeight: 600, documentHeight: document, newestID: column, newestBottom: document - 8)
    }

    @Test func aColumnAtItsEndFollowsItsOwnGrowth() {
        // The 실행 column at its end as tool calls arrive: shown to its new end each time.
        var watch = MightyGraphFollow.ColumnWatch()
        #expect(watch.appeared(board: "b", newest: true, column: "execution") == .card("execution"))
        #expect(watch.observe(board: "b", newest: true, column: "execution", page("execution", origin: 1_400, document: 2_000), userScrolling: false) == nil)
        #expect(watch.observe(board: "b", newest: true, column: "execution", page("execution", origin: 1_400, document: 2_300), userScrolling: false) == .bottom)
        #expect(watch.following("execution"))
        // A short column at its top is at its end too, and follows.
        #expect(watch.observe(board: "b", newest: true, column: "result", page("result", origin: 0, document: 200), userScrolling: false) == nil)
        #expect(watch.following("result"))
    }

    @Test func aUsersMoveStopsItsColumnOnly() {
        var watch = MightyGraphFollow.ColumnWatch()
        _ = watch.observe(board: "b", newest: true, column: "execution", page("execution", origin: 1_400, document: 2_000), userScrolling: false)
        _ = watch.observe(board: "b", newest: true, column: "request", page("request", origin: 900, document: 1_500), userScrolling: false)
        // A wheel moves the 실행 column 200pt up: it stops; the 요청 column follows on.
        watch.userScrolled("execution", byY: -200)
        #expect(!watch.following("execution"))
        #expect(watch.following("request"))
        // Both grow: only the 요청 column is shown to its end; the reader of 실행 stays.
        #expect(watch.observe(board: "b", newest: true, column: "execution", page("execution", origin: 1_200, document: 2_400), userScrolling: false) == nil)
        #expect(watch.observe(board: "b", newest: true, column: "request", page("request", origin: 900, document: 1_800), userScrolling: false) == .bottom)
        // A scroll the system reports in one column is that column's.
        #expect(watch.observe(board: "b", newest: true, column: "request", page("request", origin: 600, document: 1_800), userScrolling: true) == nil)
        #expect(!watch.following("request"))
        // Coming back to its end resumes the column's follow.
        #expect(watch.observe(board: "b", newest: true, column: "execution", page("execution", origin: 1_800, document: 2_400), userScrolling: false) == nil)
        #expect(watch.following("execution"))
        #expect(!watch.following("request"))
    }

    @Test func openingATextStopsItsColumnOnly() {
        var watch = MightyGraphFollow.ColumnWatch()
        _ = watch.observe(board: "b", newest: true, column: "execution", page("execution", origin: 1_400, document: 2_000), userScrolling: false)
        watch.stop("request")
        #expect(!watch.following("request"))
        #expect(watch.following("execution"))
        #expect(watch.observe(board: "b", newest: true, column: "request", page("request", origin: 0, document: 1_200), userScrolling: false) == nil)
        #expect(watch.observe(board: "b", newest: true, column: "execution", page("execution", origin: 1_400, document: 2_300), userScrolling: false) == .bottom)
    }

    @Test func aNewRequestShowsEveryColumnsEnd() {
        var watch = MightyGraphFollow.ColumnWatch()
        _ = watch.observe(board: "p", newest: true, column: "plan", page("plan", origin: 1_400, document: 2_000), userScrolling: false)
        watch.userScrolled("plan", byY: -500)
        watch.stop("execute")
        #expect(!watch.following("plan"))
        #expect(!watch.following("execute"))
        // A styled pipeline's next request: the same board, every column back at its end.
        #expect(watch.newRequest(board: "p", column: "plan") == .card("plan"))
        #expect(watch.newRequest(board: "p", column: "execute") == .card("execute"))
        #expect(watch.following("plan"))
        #expect(watch.following("execute"))
        // The column lands short of its end at first: still following, and shown again.
        #expect(watch.observe(board: "p", newest: true, column: "plan", page("plan", origin: 900, document: 2_400), userScrolling: false) == .bottom)
        #expect(watch.observe(board: "p", newest: true, column: "plan", page("plan", origin: 1_800, document: 2_400), userScrolling: false) == nil)
        #expect(watch.following("plan"))
    }

    @Test func anOlderBoardFollowsNothingAndTheNewestFollowsAgain() {
        var watch = MightyGraphFollow.ColumnWatch()
        _ = watch.observe(board: "new", newest: true, column: "execution", page("execution", origin: 1_400, document: 2_000), userScrolling: false)
        watch.userScrolled("execution", byY: -300)
        // An older board opens at its columns' tops: none follows, and its growth moves nothing.
        #expect(watch.appeared(board: "old", newest: false, column: "execution") == nil)
        #expect(!watch.following("execution"))
        #expect(!watch.following("result"))
        #expect(watch.observe(board: "old", newest: false, column: "execution", page("execution", origin: 0, document: 2_000), userScrolling: false) == nil)
        watch.stop("execution")
        watch.userScrolled("execution", byY: 100)
        #expect(watch.boardID == "old")
        // Back to the newest board: every column shows its end and follows, whatever the reader did before.
        #expect(watch.appeared(board: "new", newest: true, column: "execution") == .card("execution"))
        #expect(watch.appeared(board: "new", newest: true, column: "result") == .card("result"))
        #expect(watch.following("execution"))
        // The board shown stops being the newest when a newer one arrives: it follows nothing from then.
        #expect(watch.observe(board: "new", newest: false, column: "execution", page("execution", origin: 1_400, document: 2_300), userScrolling: false) == nil)
        #expect(!watch.following("execution"))
    }

    @Test func comingBackShowsOnlyTheFollowingColumnsEnds() {
        var watch = MightyGraphFollow.ColumnWatch()
        _ = watch.observe(board: "b", newest: true, column: "execution", page("execution", origin: 1_400, document: 2_000), userScrolling: false)
        _ = watch.observe(board: "b", newest: true, column: "request", page("request", origin: 0, document: 300), userScrolling: false)
        watch.userScrolled("execution", byY: -400)
        // The view shows the diagram and comes back: the same board, its columns appear again.
        #expect(watch.appeared(board: "b", newest: true, column: "execution") == nil)
        #expect(watch.appeared(board: "b", newest: true, column: "request") == .card("request"))
        // The reader of 실행 is judged by where the column opened.
        #expect(watch.observe(board: "b", newest: true, column: "execution", page("execution", origin: 1_400, document: 2_000), userScrolling: false) == nil)
        #expect(watch.following("execution"))
    }
}
