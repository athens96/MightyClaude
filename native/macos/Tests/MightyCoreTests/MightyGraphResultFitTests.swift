import CoreGraphics
import Foundation
import Testing
@testable import MightyCore

/// The newest result card takes its content's height up to the size the user
/// last dragged it to (or the window fit), and arrives right above the composer.
struct MightyGraphResultFitTests {
    private func run(_ id: String, status: String = "completed") -> MightyGraphRun {
        MightyGraphRun(id: id, input: "요청 " + id, status: status, rootEntries: [], agents: [],
                       finalOutput: status == "completed" ? "결과 " + id : nil)
    }
    private func resultID(_ run: String) -> String { MightyGraphBlockSize.nodeID(runID: run, suffix: "result") }
    private func frame(_ layout: MightyGraphLayout, _ id: String) -> CGRect? { layout.nodes.first { $0.id == id }?.frame }

    // MARK: Size

    @Test func shortContentShrinksBelowTheSavedSize() {
        let cap = CGSize(width: 1_100, height: 800)
        #expect(MightyGraphLayout.resultSize(cap: cap, contentHeight: 260) == CGSize(width: 1_100, height: 260))
    }

    @Test func tallContentStopsAtTheSavedSize() {
        let cap = CGSize(width: 700, height: 350)
        #expect(MightyGraphLayout.resultSize(cap: cap, contentHeight: 2_000) == cap)
    }

    @Test func tinyContentKeepsTheMinimumHeight() {
        let cap = CGSize(width: 700, height: 350)
        #expect(MightyGraphLayout.resultSize(cap: cap, contentHeight: 60).height == MightyGraphLayout.minimumResultHeight)
        #expect(MightyGraphLayout.resultSize(cap: cap, contentHeight: 0).height == MightyGraphLayout.minimumResultHeight)
    }

    @Test func unmeasuredOrBrokenContentTakesTheCap() {
        let cap = CGSize(width: 700, height: 350)
        #expect(MightyGraphLayout.resultSize(cap: cap, contentHeight: nil) == cap)
        #expect(MightyGraphLayout.resultSize(cap: cap, contentHeight: .nan) == cap)
        #expect(MightyGraphLayout.resultSize(cap: cap, contentHeight: .infinity) == cap)
        #expect(MightyGraphLayout.resultSize(cap: cap, contentHeight: -5) == cap)
    }

    @Test func fractionalContentRoundsUpSoTheLastLineIsNeverClipped() {
        #expect(MightyGraphLayout.resultSize(cap: CGSize(width: 700, height: 900), contentHeight: 300.2).height == 301)
    }

    @Test func savedSizeIsAMaximumForEveryNewResult() {
        let viewport = CGSize(width: 1_200, height: 800)
        let saved = MightyGraphBlockSize(width: 900, height: 600)
        // Short answer: as tall as its content, at the saved width.
        let short = MightyGraphLayout.make(runs: [run("one")], draft: "", running: false, expanded: [],
                                           viewport: viewport, sharedResultSize: saved, resultContentHeight: 180)
        #expect(frame(short, resultID("one"))?.size == CGSize(width: 900, height: 180))
        // Long answer: the saved size, scrolling inside.
        let long = MightyGraphLayout.make(runs: [run("one"), run("two")], draft: "", running: false, expanded: [],
                                          viewport: viewport, sharedResultSize: saved, resultContentHeight: 1_500)
        #expect(frame(long, resultID("two"))?.size == CGSize(width: 900, height: 600))
    }

    @Test func withNothingSavedTheWindowFitIsTheMaximum() {
        let viewport = CGSize(width: 1_200, height: 800)
        let short = MightyGraphLayout.make(runs: [run("one")], draft: "", running: false, expanded: [],
                                           viewport: viewport, resultContentHeight: 240)
        #expect(frame(short, resultID("one"))?.size == CGSize(width: 1_152, height: 240))
        #expect(short.fittedResultID == resultID("one"))
        let long = MightyGraphLayout.make(runs: [run("one")], draft: "", running: false, expanded: [],
                                          viewport: viewport, resultContentHeight: 5_000)
        #expect(frame(long, resultID("one"))?.size == CGSize(width: 1_152, height: 752))
    }

    @Test func aShorterResultMovesNothingAboveItAndPullsTheDraftUp() {
        let viewport = CGSize(width: 1_200, height: 800)
        let full = MightyGraphLayout.make(runs: [run("one")], draft: "", running: false, expanded: [], viewport: viewport)
        let fitted = MightyGraphLayout.make(runs: [run("one")], draft: "", running: false, expanded: [],
                                            viewport: viewport, resultContentHeight: 200)
        let request = MightyGraphBlockSize.nodeID(runID: "one", suffix: "request")
        #expect(frame(full, request) == frame(fitted, request))
        #expect(frame(full, resultID("one"))?.minY == frame(fitted, resultID("one"))?.minY)
        let draftFull = frame(full, MightyGraphCamera.pendingNodeID)
        let draftFitted = frame(fitted, MightyGraphCamera.pendingNodeID)
        #expect(draftFull != nil && draftFitted != nil)
        let lift: CGFloat = (draftFull?.minY ?? 0) - (draftFitted?.minY ?? 0)
        #expect(abs(lift - (752 - 200)) < 0.001)
    }

    @Test func olderResultsAndNoViewportIgnoreTheMeasurement() {
        let viewport = CGSize(width: 1_200, height: 800)
        let layout = MightyGraphLayout.make(runs: [run("one"), run("two")], draft: "", running: false, expanded: [],
                                            viewport: viewport, resultContentHeight: 160)
        #expect(frame(layout, resultID("one"))?.size == CGSize(width: 500, height: 200))
        let headless = MightyGraphLayout.make(runs: [run("one")], draft: "", running: false, expanded: [],
                                              resultContentHeight: 160)
        #expect(frame(headless, resultID("one"))?.size == CGSize(width: 500, height: 200))
    }

    // MARK: Camera

    @Test func aResultThatFitsSitsRightAboveTheComposer() {
        let viewport = CGSize(width: 900, height: 700)
        let card = CGRect(x: 24, y: 1_800, width: 500, height: 300)
        for zoom in [CGFloat(0.5), 1, 1.5] {
            let offset = MightyGraphLayout.revealOffset(for: card, viewport: viewport, zoom: zoom)
            // The card's bottom lands 16pt above the viewport's bottom edge…
            #expect(abs(offset.y + card.maxY * zoom - (viewport.height - 16)) < 0.001)
            // …and it is centred as every other re-aim centres it.
            #expect(offset.x == MightyGraphLayout.cameraOffset(for: card, viewport: viewport, zoom: zoom, alignTop: true).x)
        }
    }

    @Test func aResultTallerThanTheViewportShowsItsTop() {
        let viewport = CGSize(width: 900, height: 700)
        let card = CGRect(x: 24, y: 1_800, width: 500, height: 900)
        let offset = MightyGraphLayout.revealOffset(for: card, viewport: viewport, zoom: 1)
        #expect(offset == MightyGraphLayout.cameraOffset(for: card, viewport: viewport, zoom: 1, alignTop: true))
        #expect(offset.y + card.minY == 16)
        // Exactly the viewport less both margins: top and bottom placements agree.
        let snug = CGRect(x: 24, y: 400, width: 500, height: 668)
        #expect(MightyGraphLayout.revealOffset(for: snug, viewport: viewport, zoom: 1).y + snug.minY == 16)
    }

    @Test func alignBottomWinsOverAlignTop() {
        let viewport = CGSize(width: 900, height: 700)
        let card = CGRect(x: 24, y: 600, width: 500, height: 200)
        #expect(MightyGraphLayout.cameraOffset(for: card, viewport: viewport, zoom: 1, alignTop: true, alignBottom: true)
                == MightyGraphLayout.revealOffset(for: card, viewport: viewport, zoom: 1))
        #expect(MightyGraphLayout.cameraOffset(for: card, viewport: viewport, zoom: 1, alignTop: true, alignBottom: false)
                == MightyGraphLayout.cameraOffset(for: card, viewport: viewport, zoom: 1, alignTop: true))
    }

    // MARK: When

    private typealias Progress = MightyGraphCamera.ResultReveal.RunProgress
    private func progress(_ pairs: (String, Bool)...) -> [Progress] { pairs.map { Progress(id: $0.0, finished: $0.1) } }
    private func change(_ reveal: inout MightyGraphCamera.ResultReveal, _ previous: [Progress], _ current: [Progress],
                        measured: Bool = true) -> String? {
        reveal.runsChanged(previous: previous, current: current, resultID: resultID, measured: { _ in measured })
    }

    @Test func aRequestThatFinishesWhileWatchedIsRevealedOnce() {
        var reveal = MightyGraphCamera.ResultReveal()
        #expect(change(&reveal, progress(("one", false)), progress(("one", true))) == resultID("one"))
        #expect(reveal.holdingID == resultID("one"))
        // Redraws with the same runs reveal nothing again.
        #expect(change(&reveal, progress(("one", true)), progress(("one", true))) == nil)
        // A new request starting is not a result.
        #expect(change(&reveal, progress(("one", true)), progress(("one", true), ("two", false))) == nil)
        // The next request's result is a new one.
        #expect(change(&reveal, progress(("one", true), ("two", false)), progress(("one", true), ("two", true))) == resultID("two"))
    }

    @Test func runsAPaneOpensOrHydratesWithAreNeverRevealed() {
        var reveal = MightyGraphCamera.ResultReveal()
        // Launch or restore: the runs arrive after the first render, already finished.
        #expect(change(&reveal, [], progress(("one", true), ("two", true))) == nil)
        // A run that appeared already finished.
        #expect(change(&reveal, progress(("one", true)), progress(("one", true), ("two", true))) == nil)
        #expect(change(&reveal, [], []) == nil)
        #expect(reveal.holdingID == nil)
    }

    @Test func theNewestResultMovingBackIsNeverRevealed() {
        var reveal = MightyGraphCamera.ResultReveal()
        // The newest run is no longer finished (resumed): the older result is
        // the newest again, but it did not just finish.
        #expect(change(&reveal, progress(("one", true), ("two", true)), progress(("one", true), ("two", false))) == nil)
        // The newest run removed.
        #expect(change(&reveal, progress(("one", true), ("two", true)), progress(("one", true))) == nil)
        #expect(reveal.holdingID == nil)
    }

    @Test func anUnmeasuredCardIsHeldUntilItsHeightArrives() {
        var reveal = MightyGraphCamera.ResultReveal()
        // No camera move yet: the card would land at its cap and jump again.
        #expect(change(&reveal, progress(("one", false)), progress(("one", true)), measured: false) == nil)
        #expect(reveal.holdingID == resultID("one") && reveal.awaitingMeasure)
        #expect(reveal.viewportChanged() == nil)
        #expect(reveal.contentMeasured(resultID("one")) == resultID("one"))
        #expect(!reveal.awaitingMeasure)
        // Measured: a late timeout does nothing.
        #expect(reveal.measureTimedOut(resultID("one")) == nil)
        #expect(reveal.viewportChanged() == resultID("one"))
    }

    @Test func aCardNeverMeasuredIsPlacedAtItsCapOnTimeout() {
        var reveal = MightyGraphCamera.ResultReveal()
        _ = change(&reveal, progress(("one", false)), progress(("one", true)), measured: false)
        #expect(reveal.measureTimedOut(resultID("zero")) == nil)
        #expect(reveal.measureTimedOut(resultID("one")) == resultID("one"))
        #expect(reveal.measureTimedOut(resultID("one")) == nil)
        // Its measurement, once it is drawn, places it again.
        #expect(reveal.contentMeasured(resultID("one")) == resultID("one"))
    }

    @Test func theHeldCardFollowsItsMeasurementAndTheViewport() {
        var reveal = MightyGraphCamera.ResultReveal()
        _ = change(&reveal, progress(("one", false)), progress(("one", true)))
        #expect(reveal.contentMeasured(resultID("one")) == resultID("one"))
        #expect(reveal.contentMeasured(resultID("zero")) == nil)
        #expect(reveal.viewportChanged() == resultID("one"))
    }

    @Test func theUsersOwnScrollEndsTheHold() {
        var reveal = MightyGraphCamera.ResultReveal()
        _ = change(&reveal, progress(("one", false)), progress(("one", true)), measured: false)
        reveal.cancel()
        #expect(reveal.holdingID == nil && !reveal.awaitingMeasure)
        #expect(reveal.contentMeasured(resultID("one")) == nil)
        #expect(reveal.measureTimedOut(resultID("one")) == nil)
        #expect(reveal.viewportChanged() == nil)
        // A later result is revealed again.
        #expect(change(&reveal, progress(("one", true), ("two", false)), progress(("one", true), ("two", true))) == resultID("two"))
    }

    @Test func typingADraftEndsTheHold() {
        var reveal = MightyGraphCamera.ResultReveal()
        _ = change(&reveal, progress(("one", false)), progress(("one", true)))
        reveal.draftChanged(wasEmpty: false, isEmpty: true)
        #expect(reveal.holdingID == resultID("one"))
        reveal.draftChanged(wasEmpty: true, isEmpty: true)
        #expect(reveal.holdingID == resultID("one"))
        reveal.draftChanged(wasEmpty: true, isEmpty: false)
        #expect(reveal.holdingID == nil)
    }

    @Test func theTimelineScrollsForTheSameRun() {
        typealias Reveal = MightyGraphCamera.ResultReveal
        #expect(Reveal.finishedRunID(previous: progress(("a", false)), current: progress(("a", true))) == "a")
        #expect(Reveal.finishedRunID(previous: [], current: progress(("a", true))) == nil)
        #expect(Reveal.finishedRunID(previous: progress(("a", true), ("b", true)), current: progress(("a", true), ("b", false))) == nil)
    }

    // MARK: Dragging the newest result

    @Test func aCancelledDragSavesNothing() {
        let saved = MightyGraphBlockSize(width: 900, height: 700)
        #expect(MightyGraphLayout.resultDragCap(released: CGSize(width: 900, height: 180), edges: .bottomRight, cancelled: true,
                                                saved: saved, viewport: CGSize(width: 1_200, height: 800)) == nil)
    }

    @Test func aWidthOnlyDragKeepsTheMaximumHeight() {
        let viewport = CGSize(width: 1_200, height: 800)
        // The card shows 180 tall (its content) under a saved 700 maximum.
        let saved = MightyGraphBlockSize(width: 900, height: 700)
        #expect(MightyGraphLayout.resultDragCap(released: CGSize(width: 1_000, height: 180), edges: .right, cancelled: false,
                                                saved: saved, viewport: viewport) == MightyGraphBlockSize(width: 1_000, height: 700))
        #expect(MightyGraphLayout.resultDragCap(released: CGSize(width: 800, height: 180), edges: .left, cancelled: false,
                                                saved: saved, viewport: viewport) == MightyGraphBlockSize(width: 800, height: 700))
        // Nothing saved yet: the window fit's height is the maximum in force.
        #expect(MightyGraphLayout.resultDragCap(released: CGSize(width: 1_000, height: 180), edges: .right, cancelled: false,
                                                saved: nil, viewport: viewport) == MightyGraphBlockSize(width: 1_000, height: 752))
    }

    @Test func aVerticalDragSavesWhatItWasReleasedAt() {
        let saved = MightyGraphBlockSize(width: 900, height: 700)
        let viewport = CGSize(width: 1_200, height: 800)
        #expect(MightyGraphLayout.resultDragCap(released: CGSize(width: 900, height: 320), edges: .bottomRight, cancelled: false,
                                                saved: saved, viewport: viewport) == MightyGraphBlockSize(width: 900, height: 320))
        #expect(MightyGraphLayout.resultDragCap(released: CGSize(width: 900, height: 260), edges: .top, cancelled: false,
                                                saved: saved, viewport: viewport) == MightyGraphBlockSize(width: 900, height: 260))
        // Clamped as every saved block size is.
        #expect(MightyGraphLayout.resultDragCap(released: CGSize(width: 100, height: 5_000), edges: .bottomRight, cancelled: false,
                                                saved: nil, viewport: viewport) == MightyGraphBlockSize(width: 300, height: 1_200))
    }

    @Test func theResultMinimumIsTheBlockMinimum() {
        #expect(MightyGraphLayout.minimumResultHeight == CGFloat(MightyGraphBlockSize.minimumHeight))
        #expect(MightyGraphBlockSize(width: 0, height: 0).normalized
                == MightyGraphBlockSize(width: MightyGraphBlockSize.minimumWidth, height: MightyGraphBlockSize.minimumHeight))
    }

    // MARK: After a drag

    @Test func aReleasedDragStopsPinningOnceTheCameraCarriesThePin() {
        let pin = CGPoint(x: 40, y: -300)
        // The card was laid out at a size other than the released one (it
        // shrank to its content): the pin still settles.
        #expect(MightyGraphCamera.resizePinSettled(dragging: false, pinnedCamera: pin, incomingCamera: pin))
        // The corrected camera has not come back from SwiftUI yet.
        #expect(!MightyGraphCamera.resizePinSettled(dragging: false, pinnedCamera: pin, incomingCamera: .zero))
        // Still dragging.
        #expect(!MightyGraphCamera.resizePinSettled(dragging: true, pinnedCamera: pin, incomingCamera: pin))
    }
}
