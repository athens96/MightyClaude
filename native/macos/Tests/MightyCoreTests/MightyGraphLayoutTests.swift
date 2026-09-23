import CoreGraphics
import Foundation
import Testing
@testable import MightyCore

struct MightyGraphLayoutTests {
    private func agent(_ id: String, parent: String? = nil) -> MightyGraphAgent {
        MightyGraphAgent(id: id, parentID: parent, title: id, input: "확인", status: "completed")
    }
    private func run(_ id: String, agents: [MightyGraphAgent] = [], status: String = "completed") -> MightyGraphRun {
        MightyGraphRun(id: id, input: "요청 " + id, status: status, rootEntries: [], agents: agents,
                       finalOutput: status == "completed" ? "결과 " + id : nil)
    }
    /// Three levels: one root, two children, one grandchild under the first.
    private var nested: [MightyGraphAgent] {
        [agent("root"), agent("left", parent: "root"), agent("right", parent: "root"), agent("deep", parent: "left")]
    }
    private var fiveRoots: [MightyGraphAgent] { (0..<5).map { agent("branch-\($0)") } }
    private func node(_ layout: MightyGraphLayout, _ id: String) -> MightyGraphLayout.Node? {
        layout.nodes.first { $0.id == id }
    }

    @Test func theCameraOffsetPutsTheTargetWhereEveryReAimExpectsIt() {
        let viewport = CGSize(width: 900, height: 700)
        let frame = CGRect(x: -140, y: 3_200, width: 500, height: 280)
        let tall = CGRect(x: 24, y: 40, width: 500, height: 1_200)
        for zoom in [CGFloat(0.5), 1, 1.5] {
            let top = MightyGraphLayout.cameraOffset(for: frame, viewport: viewport, zoom: zoom, alignTop: true)
            // Screen position of a node point is node * zoom + offset.
            #expect(frame.minY * zoom + top.y == 16)
            #expect(frame.minX * zoom + top.x == (viewport.width - frame.width * zoom) / 2)
            let centred = MightyGraphLayout.cameraOffset(for: frame, viewport: viewport, zoom: zoom, alignTop: false)
            #expect(frame.minY * zoom + centred.y == (viewport.height - frame.height * zoom) / 2)
            #expect(centred.x == top.x)
            // A card taller than the viewport starts at the top margin instead
            // of being centred past it.
            let clamped = MightyGraphLayout.cameraOffset(for: tall, viewport: viewport, zoom: zoom, alignTop: false)
            #expect(tall.minY * zoom + clamped.y == max(16, (viewport.height - tall.height * zoom) / 2))
        }
    }

    @Test func anEdgeLeavesTheSourceBottomAndEntersTheTargetTopThroughOneBend() {
        let layout = MightyGraphLayout.make(runs: [run("one", agents: [agent("solo")])], draft: "", running: false, expanded: [])
        let requestID = MightyGraphBlockSize.nodeID(runID: "one", suffix: "request")
        let agentID = MightyGraphBlockSize.nodeID(runID: "one", suffix: "agent:solo")
        guard let request = node(layout, requestID), let child = node(layout, agentID),
              let edge = layout.edges.first(where: { $0.source == requestID && $0.target == agentID }) else {
            Issue.record("요청에서 하위 에이전트로 가는 선이 없습니다."); return
        }
        let points = layout.route(edge)
        #expect(points.count == 4)
        #expect(points.first == CGPoint(x: request.frame.midX, y: request.frame.maxY))
        #expect(points.last == CGPoint(x: child.frame.midX, y: child.frame.minY))
        // The bend sits at most 26 points below the source.
        #expect(points[1].y == request.frame.maxY + 26)
        #expect(points[1].y == points[2].y)
        // A joining edge bends just above its target instead.
        guard let join = layout.edges.first(where: { $0.joins }), let target = node(layout, join.target) else {
            Issue.record("결과로 모이는 선이 없습니다."); return
        }
        #expect(layout.route(join)[1].y == target.frame.minY - 26)
        #expect(layout.route(MightyGraphLayout.Edge(source: "없음", target: agentID)).isEmpty)
    }

    @Test func aRequestCardKeepsItsExactXWhateverGrowsBelowIt() {
        let id = MightyGraphBlockSize.nodeID(runID: "one", suffix: "request")
        let resultID = MightyGraphBlockSize.nodeID(runID: "one", suffix: "result")
        var requestFrames: [CGRect] = []
        var resultFrames: [CGRect] = []
        for agents in [[], [agent("solo")], fiveRoots, nested] {
            let layout = MightyGraphLayout.make(runs: [run("one", agents: agents)], draft: "", running: false, expanded: [])
            guard let request = node(layout, id), let result = node(layout, resultID) else {
                Issue.record("요청·결과 카드가 없습니다."); return
            }
            requestFrames.append(request.frame)
            resultFrames.append(result.frame)
        }
        // Five root branches are far wider than a request card; the card above
        // them still starts at the same x, so nothing on screen slides sideways.
        #expect(requestFrames.allSatisfy { $0.minX == requestFrames[0].minX })
        #expect(requestFrames.allSatisfy { $0.midX == MightyGraphCamera.centreX })
        #expect(resultFrames.allSatisfy { $0.minX == resultFrames[0].minX })
        #expect(resultFrames.allSatisfy { $0.midX == MightyGraphCamera.centreX })
    }

    @Test func anEarlierRunsCardsDoNotMoveWhenALaterRunGrowsWideBranches() {
        let narrow = [run("one"), run("two")]
        let wide = [run("one"), run("two", agents: fiveRoots)]
        let before = MightyGraphLayout.make(runs: narrow, draft: "", running: false, expanded: [])
        let after = MightyGraphLayout.make(runs: wide, draft: "", running: false, expanded: [])
        for suffix in ["request", "result"] {
            let id = MightyGraphBlockSize.nodeID(runID: "one", suffix: suffix)
            guard let old = node(before, id), let new = node(after, id) else {
                Issue.record("이전 요청의 \(suffix) 카드가 없습니다."); return
            }
            #expect(old.frame == new.frame)
        }
        // The later tree is the one that reached out, to both sides of the line.
        #expect(after.originX < before.originX)
        #expect(after.size.width > before.size.width)
    }

    @Test func aWideTreeReachesLeftOfTheDefaultAndTheCanvasFollowsIt() {
        let runs = [run("one"), run("two", agents: fiveRoots)]
        let layout = MightyGraphLayout.make(runs: runs, draft: "", running: false, expanded: [],
                                            resultFilesRunID: "two")
        guard let leading = layout.nodes.map(\.frame.minX).min() else { Issue.record("빈 레이아웃입니다."); return }
        #expect(leading < MightyGraphCamera.x(for: MightyGraphCamera.requestWidth))
        #expect(leading < 0)
        #expect(layout.originX == MightyGraphCamera.originX(leadingMinX: leading))
        // The attached file list is a node like any other: inside the canvas.
        let panelID = MightyGraphBlockSize.nodeID(runID: "two", suffix: "result-files")
        #expect(node(layout, panelID) != nil)
        for card in layout.nodes {
            #expect(card.frame.minX - layout.originX >= 0)
            #expect(card.frame.maxX - layout.originX <= layout.size.width)
            #expect(card.frame.maxY <= layout.size.height)
        }
    }

    @Test func droppingTheOldestRunsKeepsEveryXAndMovesTheRestUpByOneAmount() {
        let all = ["one", "two", "three", "four"].map { run($0, agents: $0 == "three" ? nested : []) }
        let before = MightyGraphLayout.make(runs: all, draft: "", running: false, expanded: [])
        let after = MightyGraphLayout.make(runs: Array(all.dropFirst(2)), draft: "", running: false, expanded: [])
        let survivors = after.nodes.filter { $0.id != MightyGraphCamera.pendingNodeID }
        #expect(!survivors.isEmpty)
        var shifts = Set<CGFloat>()
        for card in survivors {
            guard let old = node(before, card.id) else { Issue.record("살아남은 카드 \(card.id)가 이전 레이아웃에 없습니다."); return }
            #expect(old.frame.minX == card.frame.minX)
            #expect(old.frame.width == card.frame.width)
            shifts.insert(old.frame.minY - card.frame.minY)
        }
        // One constant upward shift, and it really moved: the committed camera
        // is now below the document, which is the blank graph this guards.
        #expect(shifts.count == 1)
        #expect((shifts.first ?? 0) > 0)
    }

    @Test func aCardWiderThanTheDefaultStaysCentredOnTheCentreline() {
        let requestID = MightyGraphBlockSize.nodeID(runID: "one", suffix: "request")
        let resultID = MightyGraphBlockSize.nodeID(runID: "one", suffix: "result")
        let sizes = [requestID: MightyGraphBlockSize(width: 1_200, height: 700),
                     MightyGraphCamera.pendingNodeID: MightyGraphBlockSize(width: 900, height: 300)]
        let layout = MightyGraphLayout.make(runs: [run("one", agents: [agent("solo")])], draft: "다음 요청",
                                            running: false, expanded: [], blockSizes: sizes)
        for id in [requestID, resultID, MightyGraphCamera.pendingNodeID] {
            guard let card = node(layout, id) else { Issue.record("\(id) 카드가 없습니다."); return }
            #expect(card.frame.midX == MightyGraphCamera.centreX)
        }
        #expect(node(layout, requestID)?.frame.width == 1_200)
        #expect(node(layout, requestID)?.frame.minX == MightyGraphCamera.centreX - 600)
    }

    // MARK: - Result card auto-fit tests

    @Test func latestResultFitsViewport() {
        let viewport = CGSize(width: 1_200, height: 800)
        let layout = MightyGraphLayout.make(runs: [run("one")], draft: "", running: false, expanded: [],
                                            viewport: viewport)
        let resultID = MightyGraphBlockSize.nodeID(runID: "one", suffix: "result")
        guard let card = node(layout, resultID) else { Issue.record("결과 카드가 없습니다."); return }
        // width = max(500, 1200-48) = 1152; height = max(200, 800-48) = 752
        #expect(card.frame.width == 1_152)
        #expect(card.frame.height == 752)
        #expect(layout.fittedResultID == resultID)
    }

    @Test func fittedResultNeverBelowDefault() {
        let viewport = CGSize(width: 100, height: 50)
        let layout = MightyGraphLayout.make(runs: [run("one")], draft: "", running: false, expanded: [],
                                            viewport: viewport)
        let resultID = MightyGraphBlockSize.nodeID(runID: "one", suffix: "result")
        guard let card = node(layout, resultID) else { Issue.record("결과 카드가 없습니다."); return }
        // width = max(500, 100-48) = 500; height = max(200, 50-48) = 200
        #expect(card.frame.width == 500)
        #expect(card.frame.height == 200)
        #expect(layout.fittedResultID == resultID)
    }

    @Test func fittedResultLeavesRoomForFilesPanel() {
        let viewport = CGSize(width: 1_200, height: 800)
        let layout = MightyGraphLayout.make(runs: [run("one")], draft: "", running: false, expanded: [],
                                            resultFilesRunID: "one", viewport: viewport)
        let resultID = MightyGraphBlockSize.nodeID(runID: "one", suffix: "result")
        guard let card = node(layout, resultID) else { Issue.record("결과 카드가 없습니다."); return }
        // files panel open: filesOffset = 336; width = max(500, 1200-48-336) = 816; height = 752
        #expect(card.frame.width == 816)
        #expect(card.frame.height == 752)
        #expect(layout.fittedResultID == resultID)
    }

    @Test func previousResultShrinksWhenNewResultAppears() {
        let viewport = CGSize(width: 1_200, height: 800)
        let layout = MightyGraphLayout.make(runs: [run("one"), run("two")], draft: "", running: false,
                                            expanded: [], viewport: viewport)
        let firstID = MightyGraphBlockSize.nodeID(runID: "one", suffix: "result")
        let secondID = MightyGraphBlockSize.nodeID(runID: "two", suffix: "result")
        guard let first = node(layout, firstID), let second = node(layout, secondID) else {
            Issue.record("결과 카드가 없습니다."); return
        }
        // Only the latest (run two) is fitted; run one uses the default 500x200
        #expect(first.frame.width == 500)
        #expect(first.frame.height == 200)
        #expect(second.frame.width == 1_152)
        #expect(second.frame.height == 752)
        #expect(layout.fittedResultID == secondID)
    }

    @Test func runningFollowUpKeepsPreviousResultFitted() {
        let viewport = CGSize(width: 1_200, height: 800)
        // run "two" is still running — no result card for it yet
        let layout = MightyGraphLayout.make(runs: [run("one"), run("two", status: "running")],
                                            draft: "", running: true, expanded: [], viewport: viewport)
        let firstID = MightyGraphBlockSize.nodeID(runID: "one", suffix: "result")
        guard let card = node(layout, firstID) else { Issue.record("결과 카드가 없습니다."); return }
        // run "one" is still the latest finished; it is auto-fit
        #expect(card.frame.width == 1_152)
        #expect(card.frame.height == 752)
        #expect(layout.fittedResultID == firstID)
        // run "two" has no result node yet
        let secondID = MightyGraphBlockSize.nodeID(runID: "two", suffix: "result")
        #expect(node(layout, secondID) == nil)
    }

    @Test func failedResultAlsoFits() {
        let viewport = CGSize(width: 1_200, height: 800)
        let layout = MightyGraphLayout.make(runs: [run("one", status: "error")], draft: "", running: false,
                                            expanded: [], viewport: viewport)
        let resultID = MightyGraphBlockSize.nodeID(runID: "one", suffix: "result")
        guard let card = node(layout, resultID) else { Issue.record("실패 결과 카드가 없습니다."); return }
        #expect(card.frame.width == 1_152)
        #expect(card.frame.height == 752)
        #expect(layout.fittedResultID == resultID)
    }

    @Test func sharedResultSizeOverridesFit() {
        let viewport = CGSize(width: 1_200, height: 800)
        let shared = MightyGraphBlockSize(width: 700, height: 350)
        let layout = MightyGraphLayout.make(runs: [run("one")], draft: "", running: false, expanded: [],
                                            viewport: viewport, sharedResultSize: shared)
        let resultID = MightyGraphBlockSize.nodeID(runID: "one", suffix: "result")
        guard let card = node(layout, resultID) else { Issue.record("결과 카드가 없습니다."); return }
        // shared size overrides auto-fit; shared.normalized = (700, 350) which passes the clamp
        #expect(card.frame.width == 700)
        #expect(card.frame.height == 350)
        // fittedResultID is nil when shared size is active
        #expect(layout.fittedResultID == nil)
    }

    @Test func clearingSharedSizeReturnsToFit() {
        let viewport = CGSize(width: 1_200, height: 800)
        let layout = MightyGraphLayout.make(runs: [run("one")], draft: "", running: false, expanded: [],
                                            viewport: viewport, sharedResultSize: nil)
        let resultID = MightyGraphBlockSize.nodeID(runID: "one", suffix: "result")
        guard let card = node(layout, resultID) else { Issue.record("결과 카드가 없습니다."); return }
        // nil sharedResultSize → auto-fit
        #expect(card.frame.width == 1_152)
        #expect(card.frame.height == 752)
        #expect(layout.fittedResultID == resultID)
    }

    @Test func olderResultKeepsPerCardSize() {
        let viewport = CGSize(width: 1_200, height: 800)
        let firstResultID = MightyGraphBlockSize.nodeID(runID: "one", suffix: "result")
        let perCard = [firstResultID: MightyGraphBlockSize(width: 600, height: 300)]
        let layout = MightyGraphLayout.make(runs: [run("one"), run("two")], draft: "", running: false,
                                            expanded: [], blockSizes: perCard, viewport: viewport)
        guard let first = node(layout, firstResultID) else { Issue.record("첫 결과 카드가 없습니다."); return }
        // Older result uses its per-card saved size (600x300), not auto-fit
        #expect(first.frame.width == 600)
        #expect(first.frame.height == 300)
    }

    @Test func nilViewportKeepsTodaysSizes() {
        // nil viewport must keep the existing default 500x200 result card size
        let layout = MightyGraphLayout.make(runs: [run("one")], draft: "", running: false, expanded: [])
        let resultID = MightyGraphBlockSize.nodeID(runID: "one", suffix: "result")
        guard let card = node(layout, resultID) else { Issue.record("결과 카드가 없습니다."); return }
        #expect(card.frame.width == 500)
        #expect(card.frame.height == 200)
        #expect(layout.fittedResultID == nil)
    }

    @Test func graphResultSizeRoundTripsAndOldStateLoads() throws {
        // New state with graphResultSize encodes and decodes correctly.
        var session = RunSession(id: "s1", workspaceId: "w1", title: "테스트")
        session.graphResultSize = MightyGraphBlockSize(width: 700, height: 350)
        let data = try JSONEncoder().encode(session)
        let decoded = try JSONDecoder().decode(RunSession.self, from: data)
        #expect(decoded.graphResultSize?.width == 700)
        #expect(decoded.graphResultSize?.height == 350)

        // Old state JSON without graphResultSize decodes without error.
        let oldJSON = """
        {"id":"s2","workspaceId":"w1","title":"이전","kind":"claude","provider":"claude","model":"default",
         "status":"idle","logs":[],"settings":{"effort":"default","permissionMode":"manual"}}
        """.data(using: .utf8)!
        let old = try JSONDecoder().decode(RunSession.self, from: oldJSON)
        #expect(old.graphResultSize == nil)
    }

    @Test func fittedCardTopStaysVisibleAfterResize() {
        // After a resize the fitted card frame changes; the camera offset computed
        // with alignTop:true must place the card's top edge exactly at y=16.
        let viewport = CGSize(width: 1_200, height: 800)
        let layout = MightyGraphLayout.make(runs: [run("one")], draft: "", running: false, expanded: [],
                                            viewport: viewport)
        let resultID = MightyGraphBlockSize.nodeID(runID: "one", suffix: "result")
        guard let card = node(layout, resultID) else { Issue.record("결과 카드가 없습니다."); return }
        // Simulate a window resize to a new viewport
        let newViewport = CGSize(width: 900, height: 600)
        let newLayout = MightyGraphLayout.make(runs: [run("one")], draft: "", running: false, expanded: [],
                                               viewport: newViewport)
        guard let newCard = node(newLayout, resultID) else { Issue.record("리사이즈 후 결과 카드가 없습니다."); return }
        // The camera placed with alignTop:true keeps the card's top edge at y=16
        let offset = MightyGraphLayout.cameraOffset(for: newCard.frame, viewport: newViewport,
                                                     zoom: 1.0, alignTop: true)
        #expect(newCard.frame.minY * 1.0 + offset.y == 16)
        // New card size fits the new viewport: max(500, 900-48)=852, max(200, 600-48)=552
        #expect(newCard.frame.size == CGSize(width: 852, height: 552))
        _ = card // suppress unused warning; the before-resize card just confirms layout ran
    }
}
