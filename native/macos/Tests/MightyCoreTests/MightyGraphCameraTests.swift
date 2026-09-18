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
