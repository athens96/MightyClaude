import CoreGraphics
import Foundation

/// Where the Mighty diagram puts a tree, and when the app may re-aim its
/// camera. Both are pure rules: the graph itself can only be checked by a GUI
/// diagnostic, so everything that can be decided from ids and widths alone is
/// decided here, under unit tests.
public enum MightyGraphCamera {
    /// A request card's width. Every other horizontal number in the diagram is
    /// derived from it, so the centreline, the canvas and the tests cannot
    /// drift apart by repeating the same figure in three places.
    public static let requestWidth: CGFloat = 500
    /// The clearance the diagram keeps outside its own leading and trailing
    /// cards, and the left margin the centreline starts from.
    public static let margin: CGFloat = 24
    /// The single vertical line every tree is centred on — the left margin plus
    /// half a request card. It never depends on the widest tree, so a new
    /// branch widens its own tree to both sides (negative x included) instead
    /// of sliding every request and result card already on screen sideways.
    public static let centreX: CGFloat = margin + requestWidth / 2
    public static func x(for treeWidth: CGFloat) -> CGFloat { centreX - treeWidth / 2 }

    /// Node coordinates keep that fixed origin, so a tree wider than a request
    /// card reaches left of zero. The drawn container starts at the diagram's
    /// own leading edge instead of at zero, and this is where.
    public static func originX(leadingMinX: CGFloat) -> CGFloat { min(0, leadingMinX - margin) }
    /// The drawn container's width, measured from `originX(leadingMinX:)`. It
    /// never falls below a request card with a margin on each side.
    public static func canvasWidth(leading: CGFloat, trailing: CGFloat) -> CGFloat {
        max(trailing + margin, requestWidth + margin * 2) - originX(leadingMinX: leading)
    }

    /// A node's x inside the drawn container, which starts at the diagram's
    /// leading edge rather than at 0. Edges are routed in node coordinates too,
    /// so their whole path is translated by `drawnX(nodeX: 0, originX:)`.
    public static func drawnX(nodeX: CGFloat, originX: CGFloat) -> CGFloat { nodeX - originX }
    /// The camera offset that puts that shift back, applied outside the zoom.
    /// Together the pair is an identity: a node ends up exactly where the
    /// camera in node coordinates says it should.
    public static func drawnOffsetX(cameraX: CGFloat, originX: CGFloat, zoom: CGFloat) -> CGFloat {
        cameraX + originX * zoom
    }

    /// What the camera must do after the run list changed on its own.
    public enum Anchor: Equatable, Sendable {
        /// The document did not move under the camera; a manual position stays.
        case hold
        /// Put this block where a new request's block would be put.
        case reaim(nodeID: String, alignTop: Bool)
    }

    /// The next-request draft block, which is also the only block left once the
    /// run list empties.
    public static let pendingNodeID = "pending-input"
    /// Blocks a user can select that are not a request: the draft block, a
    /// result's attached file list, a background execution's block and a
    /// step's picture preview. They
    /// are attachments to the diagram rather than somewhere to put the camera
    /// after the whole document moved.
    public static func isAuxiliary(nodeID: String) -> Bool {
        nodeID == pendingNodeID || nodeID == MightyGraphLayout.historyNodeID || nodeID.hasSuffix(":result-files") || nodeID.contains(":" + MightyGraphLayout.executionSuffix)
            || nodeID.contains(":" + MightyGraphLayout.imagesSuffix)
            || nodeID.contains(":" + MightyGraphLayout.planRecordSuffix)
    }

    /// Above its byte budget the live history drops its OLDEST runs, so every
    /// surviving card moves up while the last run id — the only other camera
    /// trigger — stays the same. The committed offset would then sit below the
    /// end of the document and every card would be culled: a blank graph.
    /// Re-aim once, at the selected block if it survived the trim, otherwise at
    /// the newest request, aligned to the top as a new request is.
    public static func trimAnchor(previousRunIDs: [String], runIDs: [String], selectedNodeID: String?,
                                  layoutNodeIDs: Set<String>) -> Anchor {
        guard let last = runIDs.last else {
            guard !previousRunIDs.isEmpty else { return .hold }
            return reaimAnchor(newestRunID: nil, selectedNodeID: selectedNodeID, layoutNodeIDs: layoutNodeIDs)
        }
        // A new (or removed) last request keeps its own camera behaviour, and
        // streaming content alone never reaches here: no id changed.
        guard last == previousRunIDs.last, Set(previousRunIDs) != Set(runIDs) else { return .hold }
        // Older requests loaded above the first one are laid out at negative
        // y: nothing already placed moved, so the camera stays where it is.
        if runIDs.count > previousRunIDs.count, Array(runIDs.suffix(previousRunIDs.count)) == previousRunIDs { return .hold }
        return reaimAnchor(newestRunID: last, selectedNodeID: selectedNodeID, layoutNodeIDs: layoutNodeIDs)
    }

    /// Where the camera belongs once the document moved under it, whatever
    /// moved it. Every re-aim uses this one rule: the user's own block if it
    /// survived, otherwise the newest request placed as a new request is,
    /// otherwise — with no request left at all — the draft block, which is
    /// where the next one will appear. `.hold` rather than aim at nothing.
    public static func reaimAnchor(newestRunID: String?, selectedNodeID: String?, layoutNodeIDs: Set<String>) -> Anchor {
        if let selectedNodeID, !isAuxiliary(nodeID: selectedNodeID), layoutNodeIDs.contains(selectedNodeID) {
            return .reaim(nodeID: selectedNodeID, alignTop: false)
        }
        guard let newestRunID else {
            guard layoutNodeIDs.contains(pendingNodeID) else { return .hold }
            return .reaim(nodeID: pendingNodeID, alignTop: true)
        }
        let newest = MightyGraphBlockSize.nodeID(runID: newestRunID, suffix: "request")
        guard layoutNodeIDs.contains(newest) else { return .hold }
        return .reaim(nodeID: newest, alignTop: true)
    }

    /// Where the camera goes when the canvas changes size while a result card
    /// is fitting the window. Sending a request shrinks the composer, so the
    /// canvas resizes right after the new request took the camera: a block
    /// below the fitted card — that request, the draft — is newer work and
    /// keeps the camera, re-aimed because the fitted card above it changed
    /// height. Otherwise the fitted card's top is brought back into view.
    public static func resizeAnchor(fittedResultID: String?, targetID: String?, targetAlignTop: Bool,
                                    frames: [String: CGRect]) -> Anchor {
        guard let fittedResultID, let fitted = frames[fittedResultID] else { return .hold }
        if let targetID, targetID != fittedResultID, let target = frames[targetID], target.minY > fitted.minY {
            return .reaim(nodeID: targetID, alignTop: targetAlignTop)
        }
        return .reaim(nodeID: fittedResultID, alignTop: true)
    }

    /// When the camera puts a newly finished request's result card right above
    /// the composer (`MightyGraphLayout.revealOffset`). It happens once, for a
    /// request of the pane's own that finishes while the diagram watches —
    /// never for runs a pane opens or hydrates with, nor for ones read back
    /// from the session record, nor when the newest result moves back to an
    /// older request. A card whose answer was not measured yet is held without
    /// a camera move, so it lands once at its real height (or at its cap if
    /// the measurement never comes). While held, the card settling to a new
    /// height or the canvas changing size places it again. The user's own
    /// scroll, a drag, a zoom, a draft being typed, a new request or any other
    /// re-aim ends the hold.
    public struct ResultReveal: Equatable, Sendable {
        /// A request as the reveal rule sees it.
        public struct RunProgress: Equatable, Sendable {
            public var id: String
            public var finished: Bool
            public init(id: String, finished: Bool) { self.id = id; self.finished = finished }
        }
        /// The card the camera is holding above the composer.
        public private(set) var holdingID: String?
        /// The held card was not placed yet: its measurement is awaited.
        public private(set) var awaitingMeasure = false
        public init() {}

        /// The run whose result has just appeared: the newest finished run,
        /// which the previous observation saw unfinished. A run seen for the
        /// first time already finished (a launch, a hydration, history) and a
        /// newest result moving back to an earlier run never qualify.
        public static func finishedRunID(previous: [RunProgress], current: [RunProgress]) -> String? {
            guard let latest = current.last(where: \.finished),
                  let before = previous.first(where: { $0.id == latest.id }), !before.finished else { return nil }
            return latest.id
        }

        /// The pane's own runs changed. `resultID` maps a run to its result
        /// card; `measured` says whether that card's height is known already
        /// (or needs no measuring). Returns the card to place now, if any.
        public mutating func runsChanged(previous: [RunProgress], current: [RunProgress],
                                         resultID: (String) -> String, measured: (String) -> Bool) -> String? {
            guard let run = Self.finishedRunID(previous: previous, current: current) else { return nil }
            let id = resultID(run)
            holdingID = id
            awaitingMeasure = !measured(id)
            return awaitingMeasure ? nil : id
        }
        /// A card's measured content height changed; returns it when it is
        /// the one held, since its new height moved its bottom.
        public mutating func contentMeasured(_ id: String) -> String? {
            guard id == holdingID else { return nil }
            awaitingMeasure = false
            return id
        }
        /// The measurement did not come (the card was never drawn): place it
        /// at its cap, which draws it, and its measurement places it again.
        public mutating func measureTimedOut(_ id: String) -> String? {
            guard id == holdingID, awaitingMeasure else { return nil }
            awaitingMeasure = false
            return id
        }
        /// The canvas changed size; returns the held card to place again,
        /// unless it is still waiting for its first placement.
        public func viewportChanged() -> String? { awaitingMeasure ? nil : holdingID }
        /// The composer started holding a draft: the draft's block is what
        /// the camera keeps from now on.
        public mutating func draftChanged(wasEmpty: Bool, isEmpty: Bool) {
            if wasEmpty, !isEmpty { cancel() }
        }
        /// The user moved or zoomed the camera, or something else re-aimed it.
        public mutating func cancel() { holdingID = nil; awaitingMeasure = false }
    }

    /// A finished block drag keeps the dragged corner pinned until the first
    /// pass after release whose camera already carries that pin. The size is
    /// not compared: the laid-out card may differ from the released size (the
    /// newest result shrinks to its content), and a pin kept for a size that
    /// never comes would hold the camera on that card for good.
    public static func resizePinSettled(dragging: Bool, pinnedCamera: CGPoint, incomingCamera: CGPoint) -> Bool {
        !dragging && pinnedCamera == incomingCamera
    }

    /// The run-id rule above names one cause. This one names none: after a
    /// layout pass that moved cards under a camera nobody touched, did every
    /// card leave the viewport? A user who deliberately panned into empty
    /// space had nothing visible before either, so they are never corrected.
    public static func isStranded(previousFrames: [CGRect], currentFrames: [CGRect], camera: CGPoint,
                                  viewport: CGSize, zoom: CGFloat) -> Bool {
        lostFrameIndex(previousFrames: previousFrames, currentFrames: currentFrames, camera: camera, viewport: viewport, zoom: zoom) != nil
    }

    /// The previous frame the user saw most of, when the pass stranded them:
    /// the re-aim goes back to that card rather than somewhere they never were.
    public static func lostFrameIndex(previousFrames: [CGRect], currentFrames: [CGRect], camera: CGPoint,
                                      viewport: CGSize, zoom: CGFloat) -> Int? {
        guard zoom.isFinite, zoom > 0, camera.x.isFinite, camera.y.isFinite,
              viewport.width > 0, viewport.height > 0, viewport.width.isFinite, viewport.height.isFinite,
              !currentFrames.isEmpty else { return nil }
        let visible = CGRect(x: -camera.x / zoom, y: -camera.y / zoom,
                             width: viewport.width / zoom, height: viewport.height / zoom)
        func shown(_ frame: CGRect, atLeast points: CGFloat) -> CGFloat? {
            let shared = frame.intersection(visible), least = points / zoom
            guard !shared.isNull, shared.width >= min(least, frame.width), shared.height >= min(least, frame.height) else { return nil }
            return shared.width * shared.height
        }
        // A corner of one card is somewhere the user parked, not a view of the
        // graph: a small nudge would strand it and the re-aim would be a yank.
        var best: (index: Int, area: CGFloat)?
        for (index, frame) in previousFrames.enumerated() {
            guard let area = shown(frame, atLeast: strandedOverlap), area > (best?.area ?? -1) else { continue }
            best = (index, area)
        }
        guard let best else { return nil }
        // A hairline of a card is a blank screen to whoever is reading it.
        return currentFrames.contains { shown($0, atLeast: strandedResidue) != nil } ? nil : best.index
    }

    /// Screen points of a card that must have been showing before its loss counts.
    public static let strandedOverlap: CGFloat = 24
    /// Less than this left of every card reads as a blank screen.
    public static let strandedResidue: CGFloat = 4

    /// The memory the stranded rule needs between layout passes. A pass is
    /// judged against the last frames seen through the SAME camera: whoever
    /// moved the camera since — the user or an admitted target — owns what it
    /// shows, and a pass that arrives mid-interaction is judged later rather
    /// than dropped, so the net cannot be disarmed by bad timing.
    public struct StrandedWatch {
        public struct Loss: Equatable {
            /// The card the user saw most of before the layout took it away.
            public let lookedAtNodeID: String
            public let newestRunID: String?
        }
        private var frames: [CGRect] = []
        private var nodeIDs: [String] = []
        private var camera: CGPoint?
        private var zoom: CGFloat = 1
        private var newestRunID: String?
        /// A move arrived while unsettled and still waits for its verdict.
        public private(set) var isWithholding = false
        public init() {}

        /// `settled` is false while a drag, a resize, a recent user move or a
        /// pending camera target is still deciding where the camera goes.
        public mutating func observe(nodes: [(String, CGRect)], camera: CGPoint, zoom: CGFloat, viewport: CGSize,
                                     newestRunID: String?, settled: Bool) -> Loss? {
            // The draft block comes and goes with the composer's text: it is
            // neither something that moved nor something the user lost.
            let after = nodes.filter { $0.0 != MightyGraphCamera.pendingNodeID }
            // A new or removed newest request has its own camera rule.
            guard camera == self.camera, zoom == self.zoom, newestRunID == self.newestRunID else {
                rebase(after, camera: camera, zoom: zoom, newestRunID: newestRunID)
                return nil
            }
            let moved = nodeIDs.count != after.count || zip(zip(nodeIDs, frames), after).contains { $0.0 != $1.0 || $0.1 != $1.1 }
            // Back at the baseline: there is no longer a move to judge.
            guard moved else { isWithholding = false; return nil }
            guard settled else { isWithholding = true; return nil }
            let lost = MightyGraphCamera.lostFrameIndex(previousFrames: frames, currentFrames: after.map(\.1),
                                                        camera: camera, viewport: viewport, zoom: zoom)
            let loss = lost.map { Loss(lookedAtNodeID: nodeIDs[$0], newestRunID: newestRunID) }
            rebase(after, camera: camera, zoom: zoom, newestRunID: newestRunID)
            return loss
        }

        private mutating func rebase(_ nodes: [(String, CGRect)], camera: CGPoint, zoom: CGFloat, newestRunID: String?) {
            nodeIDs = nodes.map(\.0); frames = nodes.map(\.1)
            self.camera = camera; self.zoom = zoom; self.newestRunID = newestRunID
            isWithholding = false
        }
    }

    /// The camera shows the top of the diagram — the history block, or the
    /// first card when there is none — so scrolling further up asks for older
    /// requests. `slack` is how far below that top the viewport may start.
    public static func showsTop(camera: CGPoint, zoom: CGFloat, top: CGFloat, slack: CGFloat = 8) -> Bool {
        guard zoom.isFinite, zoom > 0, camera.y.isFinite, top.isFinite else { return false }
        return -camera.y / zoom <= top + slack
    }

    /// A second trim must re-aim again even when it lands on the same block, so
    /// the admission token carries the trim's own sequence number.
    public static func trimToken(sequence: Int, nodeID: String) -> String { "trim:\(sequence):\(nodeID)" }

    /// A user event can land between a new camera target being published and
    /// the app's own deferred admission of it. Committing the offset the event
    /// started from would lose the re-aim and consume its token with it, so the
    /// target is admitted first and the user's own movement goes on top.
    /// nil means the target cannot be placed yet: leave its token pending.
    public static func admittedCamera(targetToken: String?, consumedToken: String?, targetCamera: CGPoint?,
                                      current: CGPoint, requested: CGPoint) -> CGPoint? {
        guard let targetToken, targetToken != consumedToken else { return requested }
        guard let targetCamera else { return nil }
        return CGPoint(x: targetCamera.x + requested.x - current.x, y: targetCamera.y + requested.y - current.y)
    }
}
