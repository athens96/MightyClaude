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

    @Test func thePendingPlanFitsTheWindowAsTheResultDoesAndEachKeepsItsOwnSize() {
        let viewport = CGSize(width: 1_200, height: 800)
        let runs = [run("one"), run("two", status: "running")]
        let plan = MightyGraphBlockSize.nodeID(runID: "two", suffix: MightyGraphLayout.planSuffix)
        func make(result: MightyGraphBlockSize? = nil, plan planSize: MightyGraphBlockSize? = nil) -> MightyGraphLayout {
            MightyGraphLayout.make(runs: runs, draft: "", running: true, expanded: [], viewport: viewport, zoom: 1,
                                   sharedResultSize: result, resultContentHeight: 5_000, planRunID: "two", planSize: planSize)
        }
        // Nothing saved: both take the window fit.
        #expect(frame(make(), plan)?.size == CGSize(width: 1_152, height: 752))
        #expect(frame(make(), resultID("one"))?.size == CGSize(width: 1_152, height: 752))
        // A result size leaves the plan at the window fit, and a plan size leaves the result.
        #expect(frame(make(result: .init(width: 700, height: 400)), plan)?.size == CGSize(width: 1_152, height: 752))
        #expect(frame(make(plan: .init(width: 700, height: 400)), resultID("one"))?.size == CGSize(width: 1_152, height: 752))
        #expect(frame(make(plan: .init(width: 700, height: 400)), plan)?.size == CGSize(width: 700, height: 400))
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

    // MARK: Kept within the pane

    private func latest(_ layout: MightyGraphLayout) -> CGSize? { frame(layout, resultID("one"))?.size }
    private func layout(viewport: CGSize, zoom: CGFloat?, saved: MightyGraphBlockSize?, content: CGFloat? = 5_000,
                        files: Bool = false) -> MightyGraphLayout {
        MightyGraphLayout.make(runs: [run("one")], draft: "", running: false, expanded: [], resultFilesRunID: files ? "one" : nil,
                               viewport: viewport, zoom: zoom, sharedResultSize: saved, resultContentHeight: content)
    }

    @Test func aSavedSizeLargerThanThePaneIsKeptWithinIt() {
        let pane = CGSize(width: 600, height: 400)
        let saved = MightyGraphBlockSize(width: 900, height: 700)
        // Long answer: the pane less its margins, in both directions.
        #expect(latest(layout(viewport: pane, zoom: 1, saved: saved)) == CGSize(width: 552, height: 352))
        // Short answer: still as tall as its content, at the pane's width.
        #expect(latest(layout(viewport: pane, zoom: 1, saved: saved, content: 200)) == CGSize(width: 552, height: 200))
        // Unmeasured: the limited cap, not the saved size.
        #expect(latest(layout(viewport: pane, zoom: 1, saved: saved, content: nil)) == CGSize(width: 552, height: 352))
        // Only one side too big: only that side shrinks.
        #expect(latest(layout(viewport: CGSize(width: 1_200, height: 400), zoom: 1, saved: saved)) == CGSize(width: 900, height: 352))
        // No zoom given (Windows, the parity vectors): the saved size whole, as before.
        #expect(latest(layout(viewport: pane, zoom: nil, saved: saved)) == CGSize(width: 900, height: 700))
    }

    @Test func theCardShrinksWithThePaneAndGrowsBackToTheSavedSize() {
        let saved = MightyGraphBlockSize(width: 900, height: 600)
        let wide = CGSize(width: 1_200, height: 800)
        #expect(latest(layout(viewport: wide, zoom: 1, saved: saved)) == CGSize(width: 900, height: 600))
        #expect(latest(layout(viewport: CGSize(width: 500, height: 300), zoom: 1, saved: saved)) == CGSize(width: 452, height: 252))
        #expect(latest(layout(viewport: CGSize(width: 800, height: 500), zoom: 1, saved: saved)) == CGSize(width: 752, height: 452))
        // The saved size was never changed, so the card is back at it.
        #expect(latest(layout(viewport: wide, zoom: 1, saved: saved)) == CGSize(width: 900, height: 600))
        // The window fit shrinks with a narrow pane the same way, below its own 500 × 200.
        #expect(latest(layout(viewport: CGSize(width: 420, height: 230), zoom: 1, saved: nil)) == CGSize(width: 372, height: 182))
        #expect(latest(layout(viewport: wide, zoom: 1, saved: nil)) == CGSize(width: 1_152, height: 752))
    }

    @Test func theLimitIsInDiagramCoordinatesAtTheZoom() {
        let pane = CGSize(width: 900, height: 700)
        let saved = MightyGraphBlockSize(width: 1_100, height: 900)
        // Zoomed out, the pane shows more of the diagram: the saved size fits.
        #expect(latest(layout(viewport: pane, zoom: 0.5, saved: saved)) == CGSize(width: 1_100, height: 900))
        // Zoomed in, it shows less: (900 − 48) / 1.5 × (700 − 48) / 1.5.
        let zoomedIn = latest(layout(viewport: pane, zoom: 1.5, saved: saved))
        #expect(zoomedIn.map { abs($0.width - 568) < 0.001 && abs($0.height - 652 / 1.5) < 0.001 } == true)
        // The window fit is never larger than at 100% when zoomed out…
        #expect(latest(layout(viewport: pane, zoom: 0.5, saved: nil)) == CGSize(width: 852, height: 652))
        // …and, zoomed in, it is drawn inside the pane too.
        #expect(latest(layout(viewport: pane, zoom: 1.5, saved: nil)) == zoomedIn)
        for zoom in [CGFloat(0.5), 1, 1.5] {
            for saved in [saved, nil] {
                let graph = layout(viewport: pane, zoom: zoom, saved: saved)
                guard let card = frame(graph, resultID("one")) else { Issue.record("no result card"); continue }
                #expect(card.width * zoom <= pane.width - 48 + 0.001)
                #expect(card.height * zoom <= pane.height - 48 + 0.001)
                // The reveal still puts its bottom right above the composer, its top in view.
                let offset = MightyGraphLayout.revealOffset(for: card, viewport: pane, zoom: zoom)
                #expect(abs(offset.y + card.maxY * zoom - (pane.height - 16)) < 0.001)
                #expect(offset.y + card.minY * zoom >= 16 - 0.001)
            }
        }
    }

    @Test func aTinyPaneKeepsTheBlockMinimum() {
        let minimum = CGSize(width: MightyGraphBlockSize.minimumWidth, height: MightyGraphBlockSize.minimumHeight)
        #expect(MightyGraphLayout.resultViewportLimit(viewport: CGSize(width: 200, height: 100), zoom: 1, filesPanelOpen: false) == minimum)
        #expect(MightyGraphLayout.resultViewportLimit(viewport: CGSize(width: 400, height: 220), zoom: 1.5, filesPanelOpen: false) == minimum)
        let saved = MightyGraphBlockSize(width: 900, height: 700)
        #expect(latest(layout(viewport: CGSize(width: 200, height: 100), zoom: 1, saved: saved)) == minimum)
        #expect(latest(layout(viewport: CGSize(width: 200, height: 100), zoom: 1, saved: saved, content: 60)) == minimum)
        // The files panel beside the card takes its share of the width, down to the minimum.
        #expect(latest(layout(viewport: CGSize(width: 1_000, height: 600), zoom: 1, saved: saved, files: true))?.width == CGFloat(1_000 - 48 - 336))
        #expect(latest(layout(viewport: CGSize(width: 600, height: 600), zoom: 1, saved: saved, files: true))?.width == minimum.width)
        // A broken zoom counts as 100%.
        for zoom in [CGFloat(0), -1, .nan, .infinity] {
            #expect(MightyGraphLayout.resultViewportLimit(viewport: CGSize(width: 600, height: 400), zoom: zoom, filesPanelOpen: false)
                    == CGSize(width: 552, height: 352))
        }
    }

    /// A drag as the view hands it on (`MightyGraphView.resize`): limits from the layout it started on.
    private func drag(_ dragged: CGSize, _ edges: ResizeEdges, _ phase: MightyGraphLayout.ResizePhase,
                      on graph: MightyGraphLayout, saved: MightyGraphBlockSize?) -> (live: MightyGraphBlockSize?, save: MightyGraphBlockSize?) {
        MightyGraphLayout.resultDrag(dragged: dragged, edges: edges, phase: phase, saved: saved,
                                     limit: graph.resultLimit, windowFit: graph.resultWindowFit)
    }
    private let narrow = CGSize(width: 600, height: 400)
    private let bigSaved = MightyGraphBlockSize(width: 900, height: 700)

    @Test func theLayoutCarriesTheLimitsADragUses() {
        let graph = layout(viewport: narrow, zoom: 1, saved: bigSaved)
        #expect(graph.resultLimit == CGSize(width: 552, height: 352))
        #expect(graph.resultWindowFit == CGSize(width: 552, height: 352))
        let wide = layout(viewport: CGSize(width: 1_200, height: 800), zoom: 1.5, saved: bigSaved, files: true)
        #expect(wide.resultLimit == MightyGraphLayout.resultViewportLimit(viewport: CGSize(width: 1_200, height: 800), zoom: 1.5, filesPanelOpen: true))
        #expect(wide.resultWindowFit == MightyGraphLayout.resultFitSize(viewport: CGSize(width: 1_200, height: 800), filesPanelOpen: true))
        // No zoom: no limit.
        #expect(layout(viewport: narrow, zoom: nil, saved: bigSaved).resultLimit == nil)
    }

    @Test func aLiveDragFollowsTheCursorWithinThePaneOnly() {
        let graph = layout(viewport: narrow, zoom: 1, saved: bigSaved)
        // Inside the pane: exactly the dragged size.
        #expect(drag(CGSize(width: 480, height: 300), .bottomRight, .live, on: graph, saved: bigSaved).live
                == MightyGraphBlockSize(width: 480, height: 300))
        // Past it: stops at the pane, side by side.
        #expect(drag(CGSize(width: 1_000, height: 300), .bottomRight, .live, on: graph, saved: bigSaved).live
                == MightyGraphBlockSize(width: 552, height: 300))
        let pushed = drag(CGSize(width: 1_000, height: 900), .bottomRight, .live, on: graph, saved: bigSaved)
        #expect(pushed.live == MightyGraphBlockSize(width: 552, height: 352) && pushed.save == nil)
        // The live size laid out as the shared size is the card shown: inside the pane.
        let shown = layout(viewport: narrow, zoom: 1, saved: pushed.live)
        #expect(latest(shown) == CGSize(width: 552, height: 352))
        // Without a limit the cursor is followed as before.
        #expect(MightyGraphLayout.resultDrag(dragged: CGSize(width: 1_000, height: 900), edges: .bottomRight, phase: .live,
                                             saved: nil, limit: nil, windowFit: nil).live == MightyGraphBlockSize(width: 1_000, height: 900))
    }

    @Test func aCornerDragThatOnlyChangesTheHeightKeepsTheSavedWidth() {
        // The narrow pane shows the 900-wide card 552 wide; dx = 0 releases at 552.
        let graph = layout(viewport: narrow, zoom: 1, saved: bigSaved)
        let released = drag(CGSize(width: 552, height: 300), .bottomRight, .finished, on: graph, saved: bigSaved)
        #expect(released.live == nil)
        #expect(released.save == MightyGraphBlockSize(width: 900, height: 300))
        // Grown back, the card is the saved width again.
        #expect(latest(layout(viewport: CGSize(width: 1_200, height: 800), zoom: 1, saved: released.save))?.width == 900)
    }

    @Test func pushingPastThePaneKeepsTheLargerSavedSize() {
        let graph = layout(viewport: narrow, zoom: 1, saved: bigSaved)
        #expect(drag(CGSize(width: 1_000, height: 900), .bottomRight, .finished, on: graph, saved: bigSaved).save == bigSaved)
        // A saved size smaller than the pane, pushed past it: the pane's size.
        let small = MightyGraphBlockSize(width: 400, height: 250)
        let roomy = layout(viewport: narrow, zoom: 1, saved: small)
        #expect(drag(CGSize(width: 800, height: 600), .bottomRight, .finished, on: roomy, saved: small).save
                == MightyGraphBlockSize(width: 552, height: 352))
        // Nothing saved, a pane narrower than the window fit: the fit, not the shrunk size.
        let tiny = layout(viewport: CGSize(width: 420, height: 230), zoom: 1, saved: nil)
        #expect(drag(CGSize(width: 900, height: 600), .bottomRight, .finished, on: tiny, saved: nil).save
                == MightyGraphBlockSize(width: 500, height: 200))
    }

    @Test func draggingInsideThePaneSavesWhatItWasReleasedAt() {
        let graph = layout(viewport: narrow, zoom: 1, saved: bigSaved)
        #expect(drag(CGSize(width: 500, height: 300), .bottomRight, .finished, on: graph, saved: bigSaved).save
                == MightyGraphBlockSize(width: 500, height: 300))
        // Inward on one side only: the other side keeps the saved size the pane hid.
        #expect(drag(CGSize(width: 500, height: 352), .right, .finished, on: graph, saved: bigSaved).save
                == MightyGraphBlockSize(width: 500, height: 700))
        #expect(drag(CGSize(width: 552, height: 300), .bottom, .finished, on: graph, saved: bigSaved).save
                == MightyGraphBlockSize(width: 900, height: 300))
        // Zoomed in, the limit is in diagram coordinates: 568 wide at 150%.
        let zoomed = layout(viewport: CGSize(width: 900, height: 700), zoom: 1.5, saved: bigSaved)
        #expect(drag(CGSize(width: 568, height: 300), .bottomRight, .finished, on: zoomed, saved: bigSaved).save
                == MightyGraphBlockSize(width: 900, height: 300))
        #expect(drag(CGSize(width: 520, height: 300), .bottomRight, .finished, on: zoomed, saved: bigSaved).save
                == MightyGraphBlockSize(width: 520, height: 300))
    }

    @Test func aTopOrBottomDragWithNothingSavedKeepsTheWindowFitWidth() {
        // A 420-wide pane shows the 500-wide window fit 372 wide.
        let tiny = layout(viewport: CGSize(width: 420, height: 230), zoom: 1, saved: nil)
        #expect(drag(CGSize(width: 372, height: 160), .bottom, .finished, on: tiny, saved: nil).save
                == MightyGraphBlockSize(width: 500, height: 160))
        #expect(drag(CGSize(width: 340, height: 182), .left, .finished, on: tiny, saved: nil).save
                == MightyGraphBlockSize(width: 340, height: 200))
    }

    @Test func aSavedCardThePaneBoundsFollowsThePaneOnResize() {
        // Bounded: the card's size follows the viewport, so the camera keeps its top in view.
        let bound = layout(viewport: narrow, zoom: 1, saved: bigSaved)
        #expect(bound.fittedResultID == nil)
        #expect(bound.viewportBoundResultID == resultID("one"))
        let frames = Dictionary(bound.nodes.map { ($0.id, $0.frame) }, uniquingKeysWith: { first, _ in first })
        #expect(MightyGraphCamera.resizeAnchor(fittedResultID: bound.fittedResultID ?? bound.viewportBoundResultID,
                                               targetID: MightyGraphBlockSize.nodeID(runID: "one", suffix: "request"),
                                               targetAlignTop: false, frames: frames)
                == .reaim(nodeID: resultID("one"), alignTop: true))
        // Within the pane: its saved size decides, and the camera holds as before.
        let free = layout(viewport: CGSize(width: 1_200, height: 800), zoom: 1, saved: bigSaved)
        #expect(free.viewportBoundResultID == nil)
        let freeFrames = Dictionary(free.nodes.map { ($0.id, $0.frame) }, uniquingKeysWith: { first, _ in first })
        #expect(MightyGraphCamera.resizeAnchor(fittedResultID: free.fittedResultID ?? free.viewportBoundResultID,
                                               targetID: nil, targetAlignTop: false, frames: freeFrames) == .hold)
        // Bounded in one direction is enough; no zoom (no limit) never bounds.
        #expect(layout(viewport: CGSize(width: 1_200, height: 400), zoom: 1, saved: bigSaved).viewportBoundResultID == resultID("one"))
        #expect(layout(viewport: narrow, zoom: nil, saved: bigSaved).viewportBoundResultID == nil)
        // A fitted card (nothing saved) bound by the pane is reported both ways; fitted wins in the view.
        let fitted = layout(viewport: CGSize(width: 900, height: 700), zoom: 1.5, saved: nil)
        #expect(fitted.fittedResultID == resultID("one") && fitted.viewportBoundResultID == resultID("one"))
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

    private func release(_ released: CGSize, _ edges: ResizeEdges, saved: MightyGraphBlockSize?, viewport: CGSize? = nil,
                         phase: MightyGraphLayout.ResizePhase = .finished) -> MightyGraphBlockSize? {
        MightyGraphLayout.resultDrag(dragged: released, edges: edges, phase: phase, saved: saved, limit: nil,
                                     windowFit: viewport.map { MightyGraphLayout.resultFitSize(viewport: $0, filesPanelOpen: false) }).save
    }

    @Test func aCancelledDragSavesNothing() {
        let saved = MightyGraphBlockSize(width: 900, height: 700)
        #expect(release(CGSize(width: 900, height: 180), .bottomRight, saved: saved, viewport: CGSize(width: 1_200, height: 800), phase: .cancelled) == nil)
        #expect(MightyGraphLayout.resultDrag(dragged: CGSize(width: 900, height: 180), edges: .bottomRight, phase: .cancelled,
                                             saved: saved, limit: nil, windowFit: nil).live == nil)
    }

    @Test func aWidthOnlyDragKeepsTheMaximumHeight() {
        let viewport = CGSize(width: 1_200, height: 800)
        // The card shows 180 tall (its content) under a saved 700 maximum.
        let saved = MightyGraphBlockSize(width: 900, height: 700)
        #expect(release(CGSize(width: 1_000, height: 180), .right, saved: saved, viewport: viewport) == MightyGraphBlockSize(width: 1_000, height: 700))
        #expect(release(CGSize(width: 800, height: 180), .left, saved: saved, viewport: viewport) == MightyGraphBlockSize(width: 800, height: 700))
        // Nothing saved yet: the window fit's height is the maximum in force.
        #expect(release(CGSize(width: 1_000, height: 180), .right, saved: nil, viewport: viewport) == MightyGraphBlockSize(width: 1_000, height: 752))
    }

    @Test func aVerticalDragSavesWhatItWasReleasedAt() {
        let saved = MightyGraphBlockSize(width: 900, height: 700)
        let viewport = CGSize(width: 1_200, height: 800)
        #expect(release(CGSize(width: 900, height: 320), .bottomRight, saved: saved, viewport: viewport) == MightyGraphBlockSize(width: 900, height: 320))
        #expect(release(CGSize(width: 900, height: 260), .top, saved: saved, viewport: viewport) == MightyGraphBlockSize(width: 900, height: 260))
        // Clamped as every saved block size is.
        #expect(release(CGSize(width: 100, height: 5_000), .bottomRight, saved: nil, viewport: viewport) == MightyGraphBlockSize(width: 300, height: 1_200))
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
