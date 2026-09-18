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
    /// Blocks a user can select that are not a request: the draft block and a
    /// result's attached file list. They are attachments to the diagram rather
    /// than somewhere to put the camera after the whole document moved.
    public static func isAuxiliary(nodeID: String) -> Bool {
        nodeID == pendingNodeID || nodeID.hasSuffix(":result-files")
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
            // The list emptied: every card the camera knew is gone, and the
            // draft block is where the next request will appear.
            guard !previousRunIDs.isEmpty, layoutNodeIDs.contains(pendingNodeID) else { return .hold }
            return .reaim(nodeID: pendingNodeID, alignTop: true)
        }
        // A new (or removed) last request keeps its own camera behaviour, and
        // streaming content alone never reaches here: no id changed.
        guard last == previousRunIDs.last, Set(previousRunIDs) != Set(runIDs) else { return .hold }
        if let selectedNodeID, !isAuxiliary(nodeID: selectedNodeID), layoutNodeIDs.contains(selectedNodeID) {
            return .reaim(nodeID: selectedNodeID, alignTop: false)
        }
        let newest = MightyGraphBlockSize.nodeID(runID: last, suffix: "request")
        guard layoutNodeIDs.contains(newest) else { return .hold }
        return .reaim(nodeID: newest, alignTop: true)
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
