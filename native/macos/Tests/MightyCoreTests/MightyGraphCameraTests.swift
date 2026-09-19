import CoreGraphics
import Foundation
import Testing
@testable import MightyCore

struct MightyGraphCameraTests {
    private func requestID(_ run: String) -> String { MightyGraphBlockSize.nodeID(runID: run, suffix: "request") }
    private func layout(_ runs: [String]) -> Set<String> { Set(runs.map(requestID)) }

    @Test func everyTreeKeepsTheSameCentreSoCardsNeverSlideSideways() {
        // A request card is centred in its own tree, so its left edge is the
        // same number whatever the branches below it do.
        let card = MightyGraphCamera.requestWidth
        for treeWidth in [card, 752, 1_144, 2_000] {
            let requestX = MightyGraphCamera.x(for: treeWidth) + (treeWidth - card) / 2
            #expect(requestX == 24)
            #expect(MightyGraphCamera.x(for: treeWidth) + treeWidth / 2 == MightyGraphCamera.centreX)
        }
        #expect(MightyGraphCamera.x(for: card) == 24)
        #expect(MightyGraphCamera.x(for: 1_144) < 0)
    }

    @Test func theCanvasStartsAtTheDiagramsLeadingEdgeAndCoversEveryCard() {
        #expect(MightyGraphCamera.originX(leadingMinX: 24) == 0)
        #expect(MightyGraphCamera.originX(leadingMinX: -298) == -322)
        #expect(MightyGraphCamera.canvasWidth(leading: -298, trailing: 846) == 1_192)
        // One request card wide: the floor is that card plus both margins.
        #expect(MightyGraphCamera.canvasWidth(leading: 24, trailing: 524) == MightyGraphCamera.requestWidth + 48)
        // Whatever the diagram reaches, the container holds all of it with the
        // same clearance on both sides.
        for (leading, trailing) in [(CGFloat(24), CGFloat(524)), (24, 900), (-298, 846), (-1_000, 1_048)] {
            let origin = MightyGraphCamera.originX(leadingMinX: leading)
            #expect(origin <= leading - 24)
            #expect(origin + MightyGraphCamera.canvasWidth(leading: leading, trailing: trailing) >= trailing + 24)
        }
    }

    @Test func theContainerShiftAndTheOffsetThatUndoesItCancelExactly() {
        // The node layer is drawn shifted by originX inside the zoom, and the
        // offset outside it puts the shift back. Whatever the diagram's leading
        // edge is, a node lands where the camera in node coordinates says.
        for originX in [CGFloat(0), -322, -1_024] {
            for zoom in [CGFloat(0.5), 1, 1.5] {
                for (nodeX, cameraX) in [(CGFloat(24), CGFloat(0)), (-298, 140), (846, -2_400)] {
                    let drawn = MightyGraphCamera.drawnX(nodeX: nodeX, originX: originX) * zoom
                        + MightyGraphCamera.drawnOffsetX(cameraX: cameraX, originX: originX, zoom: zoom)
                    #expect(abs(drawn - (nodeX * zoom + cameraX)) < 0.000_001)
                }
            }
        }
        // The edges carry a whole path in node coordinates, so they are shifted
        // by the same rule applied to the origin.
        #expect(MightyGraphCamera.drawnX(nodeX: 0, originX: -322) == 322)
        #expect(MightyGraphCamera.drawnOffsetX(cameraX: 140, originX: 0, zoom: 1.5) == 140)
    }

    @Test func aLayoutThatLeavesNothingOnScreenIsStrandedAndNothingElseIs() {
        let viewport = CGSize(width: 800, height: 600)
        let camera = CGPoint(x: -100, y: -200)
        // Visible in node coordinates at zoom 1: (100, 200) 800 x 600.
        let onScreen = CGRect(x: 120, y: 240, width: 500, height: 280)
        let farBelow = CGRect(x: 120, y: 9_000, width: 500, height: 280)
        #expect(!MightyGraphCamera.isStranded(previousFrames: [onScreen], currentFrames: [onScreen],
                                              camera: camera, viewport: viewport, zoom: 1))
        #expect(MightyGraphCamera.isStranded(previousFrames: [onScreen], currentFrames: [farBelow],
                                             camera: camera, viewport: viewport, zoom: 1))
        // The user panned into empty space themselves: nothing was visible
        // before the change either, so their position is never corrected.
        #expect(!MightyGraphCamera.isStranded(previousFrames: [farBelow], currentFrames: [farBelow],
                                              camera: camera, viewport: viewport, zoom: 1))
        // The first pass after mounting, and an emptied layout.
        #expect(!MightyGraphCamera.isStranded(previousFrames: [], currentFrames: [onScreen],
                                              camera: camera, viewport: viewport, zoom: 1))
        #expect(!MightyGraphCamera.isStranded(previousFrames: [onScreen], currentFrames: [],
                                              camera: camera, viewport: viewport, zoom: 1))
    }

    @Test func theStrandedRuleReadsTheViewportThroughTheCurrentZoom() {
        let viewport = CGSize(width: 800, height: 600)
        let camera = CGPoint(x: -100, y: -200)
        // Zoomed out the visible rect doubles: (200, 400) 1600 x 1200 in node
        // coordinates, so a card the 1x camera had lost is on screen again.
        let wideOnly = CGRect(x: 1_200, y: 1_400, width: 200, height: 120)
        #expect(MightyGraphCamera.isStranded(previousFrames: [wideOnly], currentFrames: [CGRect(x: 0, y: 9_000, width: 10, height: 10)],
                                             camera: camera, viewport: viewport, zoom: 0.5))
        #expect(!MightyGraphCamera.isStranded(previousFrames: [wideOnly], currentFrames: [wideOnly],
                                              camera: camera, viewport: viewport, zoom: 0.5))
        // Zoomed in it shrinks to (66.6, 133.3) 533 x 400 and that card is out.
        #expect(!MightyGraphCamera.isStranded(previousFrames: [wideOnly], currentFrames: [CGRect(x: 0, y: 9_000, width: 10, height: 10)],
                                              camera: camera, viewport: viewport, zoom: 1.5))
        let near = CGRect(x: 100, y: 200, width: 200, height: 120)
        #expect(MightyGraphCamera.isStranded(previousFrames: [near], currentFrames: [CGRect(x: 0, y: 9_000, width: 10, height: 10)],
                                             camera: camera, viewport: viewport, zoom: 1.5))
        // Nothing to measure against: a graph that has not been laid out yet.
        for bad in [CGSize(width: 0, height: 600), CGSize(width: 800, height: 0), CGSize(width: CGFloat.nan, height: 600)] {
            #expect(!MightyGraphCamera.isStranded(previousFrames: [near], currentFrames: [CGRect(x: 0, y: 9_000, width: 10, height: 10)],
                                                  camera: camera, viewport: bad, zoom: 1))
        }
        #expect(!MightyGraphCamera.isStranded(previousFrames: [near], currentFrames: [CGRect(x: 0, y: 9_000, width: 10, height: 10)],
                                              camera: camera, viewport: viewport, zoom: 0))
        #expect(!MightyGraphCamera.isStranded(previousFrames: [near], currentFrames: [CGRect(x: 0, y: 9_000, width: 10, height: 10)],
                                              camera: CGPoint(x: CGFloat.nan, y: 0), viewport: viewport, zoom: 1))
    }

    @Test func theStrandedRuleAsksWhetherAnyCardIsLeftNotWhetherEveryCardIs() {
        let viewport = CGSize(width: 800, height: 600)
        let camera = CGPoint(x: -100, y: -200)
        let onScreen = CGRect(x: 120, y: 240, width: 500, height: 280)
        let alsoOnScreen = CGRect(x: 300, y: 500, width: 360, height: 240)
        let farBelow = CGRect(x: 120, y: 9_000, width: 500, height: 280)
        let fartherBelow = CGRect(x: 120, y: 12_000, width: 500, height: 280)
        // One card of a long history was showing; another one took its place.
        #expect(!MightyGraphCamera.isStranded(previousFrames: [onScreen, farBelow], currentFrames: [farBelow, alsoOnScreen],
                                              camera: camera, viewport: viewport, zoom: 1))
        #expect(MightyGraphCamera.isStranded(previousFrames: [onScreen, farBelow], currentFrames: [farBelow, fartherBelow],
                                             camera: camera, viewport: viewport, zoom: 1))
    }

    @Test func aCornerOfACardIsSomewhereTheUserParkedAndIsNotCorrected() {
        let viewport = CGSize(width: 800, height: 600)
        let camera = CGPoint(x: -100, y: -200)
        let gone = CGRect(x: 120, y: 9_000, width: 500, height: 280)
        // Visible: (100, 200) 800 x 600. Ten points of a corner, then enough.
        let sliver = CGRect(x: 890, y: 790, width: 500, height: 280)
        let enough = CGRect(x: 876, y: 776, width: 500, height: 280)
        #expect(!MightyGraphCamera.isStranded(previousFrames: [sliver], currentFrames: [gone], camera: camera, viewport: viewport, zoom: 1))
        #expect(MightyGraphCamera.isStranded(previousFrames: [enough], currentFrames: [gone], camera: camera, viewport: viewport, zoom: 1))
        // The threshold is in screen points: zoomed out, 24 points cover 48 node points.
        let halfCamera = CGPoint(x: -50, y: -100)
        let thin = CGRect(x: 1_670, y: 1_370, width: 500, height: 280)
        let wide = CGRect(x: 1_650, y: 1_350, width: 500, height: 280)
        #expect(!MightyGraphCamera.isStranded(previousFrames: [thin], currentFrames: [gone], camera: halfCamera, viewport: viewport, zoom: 0.5))
        #expect(MightyGraphCamera.isStranded(previousFrames: [wide], currentFrames: [gone], camera: halfCamera, viewport: viewport, zoom: 0.5))
        // A card that still shows a sliver afterwards is not lost.
        #expect(!MightyGraphCamera.isStranded(previousFrames: [enough], currentFrames: [sliver], camera: camera, viewport: viewport, zoom: 1))
    }

    @Test func theStrandedWatchJudgesOnlyLayoutMovesUnderAStillCamera() {
        let viewport = CGSize(width: 800, height: 600)
        let onScreen = CGRect(x: 120, y: 240, width: 500, height: 280)
        let gone = CGRect(x: 120, y: 9_000, width: 500, height: 280)
        let draft = (MightyGraphCamera.pendingNodeID, CGRect(x: 120, y: 600, width: 500, height: 140))
        func observe(_ watch: inout MightyGraphCamera.StrandedWatch, _ nodes: [(String, CGRect)], camera: CGPoint = CGPoint(x: -100, y: -200),
                     zoom: CGFloat = 1, newest: String? = "one", settled: Bool = true) -> Bool {
            watch.observe(nodes: nodes, camera: camera, zoom: zoom, viewport: viewport, newestRunID: newest, settled: settled) != nil
        }
        // The first pass has nothing to compare with; the next one is judged.
        var watch = MightyGraphCamera.StrandedWatch()
        var seen = observe(&watch, [("a", onScreen)])
        #expect(!seen)
        seen = observe(&watch, [("a", gone)])
        #expect(seen)
        // It fires once per loss: the same off-screen layout again is quiet.
        seen = observe(&watch, [("a", gone)])
        #expect(!seen)

        // A pass that arrives mid-drag is judged after the drag, not dropped,
        // and says so: it may be the last pass of a burst, so the caller retries.
        watch = MightyGraphCamera.StrandedWatch()
        _ = observe(&watch, [("a", onScreen)])
        seen = observe(&watch, [("a", gone)], settled: false)
        #expect(!seen)
        #expect(watch.isWithholding)
        seen = observe(&watch, [("a", gone)])
        #expect(seen)
        #expect(!watch.isWithholding)

        // A withheld move that went back to where it was leaves nothing to judge.
        watch = MightyGraphCamera.StrandedWatch()
        _ = observe(&watch, [("a", onScreen)])
        _ = observe(&watch, [("a", gone)], settled: false)
        seen = observe(&watch, [("a", onScreen)], settled: false)
        #expect(!seen)
        #expect(!watch.isWithholding)

        // Whoever moved the camera owns what it shows: no verdict on that pass,
        // and the next one compares against what the new camera saw.
        watch = MightyGraphCamera.StrandedWatch()
        _ = observe(&watch, [("a", onScreen)])
        seen = observe(&watch, [("a", gone)], camera: CGPoint(x: -100, y: -260))
        #expect(!seen)
        seen = observe(&watch, [("a", gone)], camera: CGPoint(x: -100, y: -260))
        #expect(!seen)
        watch = MightyGraphCamera.StrandedWatch()
        _ = observe(&watch, [("a", onScreen)])
        seen = observe(&watch, [("a", gone)], zoom: 0.9)
        #expect(!seen)

        // A new newest request is aimed at by the run-id rule.
        watch = MightyGraphCamera.StrandedWatch()
        _ = observe(&watch, [("a", onScreen)])
        seen = observe(&watch, [("a", gone), ("b", gone.offsetBy(dx: 0, dy: 400))], newest: "two")
        #expect(!seen)

        // Clearing the composer removes the draft block the user was looking
        // at; nothing moved, so the camera stays where they are typing.
        watch = MightyGraphCamera.StrandedWatch()
        _ = observe(&watch, [("a", gone), draft])
        seen = observe(&watch, [("a", gone)])
        #expect(!seen)
        seen = observe(&watch, [("a", gone), draft])
        #expect(!seen)
        // The same with a real card moving in that pass: the draft block was
        // never a card to lose, so it cannot be what the user "was looking at".
        watch = MightyGraphCamera.StrandedWatch()
        _ = observe(&watch, [("a", gone), draft])
        seen = observe(&watch, [("a", gone.offsetBy(dx: 0, dy: 40))])
        #expect(!seen)
    }

    @Test func aLossNamesTheCardTheUserSawMostOf() {
        let viewport = CGSize(width: 800, height: 600)
        let camera = CGPoint(x: -100, y: -200)
        // Visible: (100, 200) 800 x 600. "big" fills far more of it than "edge".
        let big = CGRect(x: 120, y: 240, width: 500, height: 480)
        let edge = CGRect(x: 640, y: 700, width: 360, height: 240)
        let away = CGRect(x: 120, y: 9_000, width: 500, height: 280)
        var watch = MightyGraphCamera.StrandedWatch()
        _ = watch.observe(nodes: [("edge", edge), ("big", big)], camera: camera, zoom: 1, viewport: viewport, newestRunID: "one", settled: true)
        let loss = watch.observe(nodes: [("edge", away), ("big", away.offsetBy(dx: 0, dy: 600))], camera: camera, zoom: 1,
                                 viewport: viewport, newestRunID: "one", settled: true)
        #expect(loss == MightyGraphCamera.StrandedWatch.Loss(lookedAtNodeID: "big", newestRunID: "one"))
        #expect(MightyGraphCamera.lostFrameIndex(previousFrames: [edge, big], currentFrames: [away], camera: camera, viewport: viewport, zoom: 1) == 1)
        // A card smaller than the threshold counts once all of it is showing.
        let small = CGRect(x: 400, y: 400, width: 16, height: 16)
        let smallCut = CGRect(x: 892, y: 400, width: 16, height: 16)
        #expect(MightyGraphCamera.lostFrameIndex(previousFrames: [small], currentFrames: [away], camera: camera, viewport: viewport, zoom: 1) == 0)
        #expect(MightyGraphCamera.lostFrameIndex(previousFrames: [smallCut], currentFrames: [away], camera: camera, viewport: viewport, zoom: 1) == nil)
        #expect(MightyGraphCamera.lostFrameIndex(previousFrames: [.null, .zero], currentFrames: [away], camera: camera, viewport: viewport, zoom: 1) == nil)
        // A hairline left of a card is still a blank screen to the reader.
        let hairline = CGRect(x: 898, y: 240, width: 500, height: 280)
        let readable = CGRect(x: 880, y: 240, width: 500, height: 280)
        #expect(MightyGraphCamera.isStranded(previousFrames: [big], currentFrames: [hairline], camera: camera, viewport: viewport, zoom: 1))
        #expect(!MightyGraphCamera.isStranded(previousFrames: [big], currentFrames: [readable], camera: camera, viewport: viewport, zoom: 1))
    }

    @Test func everyReAimPicksTheSelectedBlockThenTheNewestRequestThenTheDraft() {
        let ids = ["one", "two", "three"]
        var nodes = layout(ids)
        nodes.insert("pending-input")
        #expect(MightyGraphCamera.reaimAnchor(newestRunID: "three", selectedNodeID: requestID("two"), layoutNodeIDs: nodes)
                    == .reaim(nodeID: requestID("two"), alignTop: false))
        #expect(MightyGraphCamera.reaimAnchor(newestRunID: "three", selectedNodeID: nil, layoutNodeIDs: nodes)
                    == .reaim(nodeID: requestID("three"), alignTop: true))
        // A selection that did not survive, and an auxiliary one, fall through.
        #expect(MightyGraphCamera.reaimAnchor(newestRunID: "three", selectedNodeID: requestID("gone"), layoutNodeIDs: nodes)
                    == .reaim(nodeID: requestID("three"), alignTop: true))
        #expect(MightyGraphCamera.reaimAnchor(newestRunID: "three", selectedNodeID: "pending-input", layoutNodeIDs: nodes)
                    == .reaim(nodeID: requestID("three"), alignTop: true))
        // No request left at all: the draft block is where the next appears.
        #expect(MightyGraphCamera.reaimAnchor(newestRunID: nil, selectedNodeID: nil, layoutNodeIDs: ["pending-input"])
                    == .reaim(nodeID: "pending-input", alignTop: true))
        #expect(MightyGraphCamera.reaimAnchor(newestRunID: nil, selectedNodeID: nil, layoutNodeIDs: []) == .hold)
        #expect(MightyGraphCamera.reaimAnchor(newestRunID: "three", selectedNodeID: nil, layoutNodeIDs: ["pending-input"]) == .hold)
    }

    @Test func streamingAndNewRequestsLeaveTheCameraAlone() {
        let ids = ["one", "two", "three"]
        // Identical lists: content streamed into a block, nothing moved.
        #expect(MightyGraphCamera.trimAnchor(previousRunIDs: ids, runIDs: ids, selectedNodeID: nil, layoutNodeIDs: layout(ids)) == .hold)
        #expect(MightyGraphCamera.trimAnchor(previousRunIDs: ids, runIDs: ids, selectedNodeID: requestID("two"), layoutNodeIDs: layout(ids)) == .hold)
        // A new request has its own camera behaviour; so has the first render.
        let appended = ids + ["four"]
        #expect(MightyGraphCamera.trimAnchor(previousRunIDs: ids, runIDs: appended, selectedNodeID: nil, layoutNodeIDs: layout(appended)) == .hold)
        #expect(MightyGraphCamera.trimAnchor(previousRunIDs: [], runIDs: ids, selectedNodeID: nil, layoutNodeIDs: layout(ids)) == .hold)
        #expect(MightyGraphCamera.trimAnchor(previousRunIDs: [], runIDs: [], selectedNodeID: nil, layoutNodeIDs: ["pending-input"]) == .hold)
    }

    @Test func aTrimReAimsAtTheSelectedBlockOrElseTheNewestRequest() {
        let before = ["one", "two", "three"]
        let after = ["two", "three"]
        // The selected block survived the trim: keep the user's own block.
        #expect(MightyGraphCamera.trimAnchor(previousRunIDs: before, runIDs: after, selectedNodeID: requestID("two"), layoutNodeIDs: layout(after))
                    == .reaim(nodeID: requestID("two"), alignTop: false))
        // It did not: the newest request, placed as a new request would be.
        #expect(MightyGraphCamera.trimAnchor(previousRunIDs: before, runIDs: after, selectedNodeID: requestID("one"), layoutNodeIDs: layout(after))
                    == .reaim(nodeID: requestID("three"), alignTop: true))
        #expect(MightyGraphCamera.trimAnchor(previousRunIDs: before, runIDs: after, selectedNodeID: nil, layoutNodeIDs: layout(after))
                    == .reaim(nodeID: requestID("three"), alignTop: true))
        // Nothing to aim at rather than aiming at the document origin.
        #expect(MightyGraphCamera.trimAnchor(previousRunIDs: before, runIDs: after, selectedNodeID: nil, layoutNodeIDs: ["pending-input"]) == .hold)
    }

    @Test func auxiliaryBlocksAreNeverTheAnchorOfATrim() {
        let before = ["one", "two", "three"]
        let after = ["two", "three"]
        let files = MightyGraphBlockSize.nodeID(runID: "three", suffix: "result-files")
        #expect(MightyGraphCamera.isAuxiliary(nodeID: "pending-input"))
        #expect(MightyGraphCamera.isAuxiliary(nodeID: files))
        #expect(!MightyGraphCamera.isAuxiliary(nodeID: requestID("three")))
        // The draft block and a result's file list are attachments, not places
        // to put the camera: both fall through to the newest request.
        var nodes = layout(after)
        nodes.formUnion(["pending-input", files])
        #expect(MightyGraphCamera.trimAnchor(previousRunIDs: before, runIDs: after, selectedNodeID: "pending-input", layoutNodeIDs: nodes)
                    == .reaim(nodeID: requestID("three"), alignTop: true))
        #expect(MightyGraphCamera.trimAnchor(previousRunIDs: before, runIDs: after, selectedNodeID: files, layoutNodeIDs: nodes)
                    == .reaim(nodeID: requestID("three"), alignTop: true))
    }

    @Test func anEmptiedRunListReAimsAtTheNextRequestBlock() {
        let ids = ["one", "two", "three"]
        // Every card the camera knew is gone; only the draft block is left.
        #expect(MightyGraphCamera.trimAnchor(previousRunIDs: ids, runIDs: [], selectedNodeID: nil, layoutNodeIDs: ["pending-input"])
                    == .reaim(nodeID: "pending-input", alignTop: true))
        #expect(MightyGraphCamera.trimAnchor(previousRunIDs: ids, runIDs: [], selectedNodeID: requestID("two"), layoutNodeIDs: ["pending-input"])
                    == .reaim(nodeID: "pending-input", alignTop: true))
        // Not even a draft block to aim at: hold rather than aim at nothing.
        #expect(MightyGraphCamera.trimAnchor(previousRunIDs: ids, runIDs: [], selectedNodeID: nil, layoutNodeIDs: []) == .hold)
    }

    @Test func eachTrimAdmitsItsOwnReAimEvenOnTheSameBlock() {
        let anchor = requestID("three")
        let first = MightyGraphCamera.trimToken(sequence: 1, nodeID: anchor)
        let second = MightyGraphCamera.trimToken(sequence: 2, nodeID: anchor)
        #expect(first != second)
        #expect(first != MightyGraphCamera.trimToken(sequence: 1, nodeID: requestID("two")))
        // The other two admission tokens the view issues never collide with it.
        #expect(first != "run:three"); #expect(first != "initial:session")
    }

    @Test func anEventDuringAPendingReAimLandsOnTheNewTargetNotTheStaleOffset() {
        // The camera the trim left behind, and where the re-aim would put it.
        let stale = CGPoint(x: 40, y: -9_000)
        let target = CGPoint(x: 40, y: 120)
        // Nothing pending: the event commits exactly what it asked for.
        #expect(MightyGraphCamera.admittedCamera(targetToken: nil, consumedToken: nil, targetCamera: target,
                                                 current: stale, requested: CGPoint(x: 55, y: -8_980)) == CGPoint(x: 55, y: -8_980))
        #expect(MightyGraphCamera.admittedCamera(targetToken: "trim:1:a", consumedToken: "trim:1:a", targetCamera: target,
                                                 current: stale, requested: stale) == stale)
        // A wheel between the new token and its deferred admission: the target
        // is admitted first and the user's own movement goes on top of it.
        #expect(MightyGraphCamera.admittedCamera(targetToken: "trim:2:a", consumedToken: "trim:1:a", targetCamera: target,
                                                 current: stale, requested: CGPoint(x: stale.x - 24, y: stale.y + 36))
                    == CGPoint(x: target.x - 24, y: target.y + 36))
        // A click commits the position it started from, which is the re-aim.
        #expect(MightyGraphCamera.admittedCamera(targetToken: "trim:2:a", consumedToken: nil, targetCamera: target,
                                                 current: stale, requested: stale) == target)
        // No frame for the target yet: nothing to commit, the token stays.
        #expect(MightyGraphCamera.admittedCamera(targetToken: "trim:2:a", consumedToken: nil, targetCamera: nil,
                                                 current: stale, requested: stale) == nil)
    }

    @Test func theLiveHistoryTrimDropsTheOldestRunsAndKeepsTheLastID() {
        // The rule's premise, from the code that actually trims: above the
        // budget the oldest runs go and the last run id survives.
        let runs = (0..<64).map { index in
            MightyGraphRun(id: "run-\(index)", input: String(repeating: "가", count: 40_000), status: "completed",
                           rootEntries: [LogEntry(id: "entry-\(index)", kind: "assistant", text: String(repeating: "x", count: 40_000))],
                           finalOutput: "결과 \(index)")
        }
        let bounded = MightyGraphSupport.boundedLiveHistory(runs)
        #expect(bounded.count < runs.count)
        #expect(bounded.last?.id == runs.last?.id)
        #expect(bounded.first?.id != runs.first?.id)
        let anchor = MightyGraphCamera.trimAnchor(previousRunIDs: runs.map(\.id), runIDs: bounded.map(\.id), selectedNodeID: nil,
                                                  layoutNodeIDs: Set(bounded.map { requestID($0.id) }))
        #expect(anchor == .reaim(nodeID: requestID(runs[runs.count - 1].id), alignTop: true))
    }

    @Test func aTrimGoesToTheLowWaterMarkSoTheFollowingAppendsNeverTrimAgain() {
        // Each trim re-aims the camera, so one crossing must buy a long quiet
        // stretch rather than a trim on every streamed event.
        let heavy = (0..<12).map { index in
            MightyGraphRun(id: "run-\(index)", input: String(repeating: "x", count: 200_000), status: "completed")
        }
        #expect(MightyGraphSupport.liveHistoryBytes(heavy) > MightyGraphSupport.liveHistoryLimit)
        var history = MightyGraphSupport.boundedLiveHistory(heavy)
        #expect(MightyGraphSupport.liveHistoryBytes(history) <= MightyGraphSupport.liveHistoryLowWater)
        #expect(history.count < heavy.count)
        // Whole oldest runs went, and the newest request stayed.
        #expect(history.map(\.id) == heavy.suffix(history.count).map(\.id))
        let settled = history.map(\.id)
        for index in 0..<40 {
            history.append(MightyGraphRun(id: "next-\(index)", input: "짧은 요청", status: "running"))
            history = MightyGraphSupport.boundedLiveHistory(history)
            #expect(history.map(\.id) == settled + (0...index).map { "next-\($0)" })
        }
    }

    @Test func oneRunOverTheWholeBudgetIsKeptAndClippedInsteadOfDropped() {
        let huge = MightyGraphRun(id: "only-run", input: String(repeating: "x", count: 3 * 1024 * 1024), status: "completed")
        let alone = MightyGraphSupport.boundedLiveHistory([huge])
        #expect(alone.count == 1)
        #expect(alone.first?.id == "only-run")
        #expect((alone.first?.input.utf8.count ?? 0) < huge.input.utf8.count)
        // The newest request survives even when it alone is over the budget.
        let crowded = (0..<6).map { index in
            MightyGraphRun(id: "old-\(index)", input: String(repeating: "y", count: 400_000), status: "completed")
        } + [huge]
        let trimmed = MightyGraphSupport.boundedLiveHistory(crowded)
        #expect(trimmed.count == 1)
        #expect(trimmed.first?.id == "only-run")
    }
}
