import CoreGraphics
import Foundation

/// Dragging the Mac 구조 view's empty space moves its page, as dragging the diagram's
/// empty canvas moves its camera: the point that was grabbed stays under the
/// pointer, and the page stops at its edges as a scroll does.
///
/// A press on selectable text or a native control is theirs: text selects, a scroller
/// scrolls. Any other press on the page waits until it moves `clickSlop` points; a
/// shorter press is a click and goes on to what was pressed (a fold toggle, a reveal
/// button), so buttons click as they always did. A pan is the user's own move, as a
/// wheel scroll is (`MightyGraphFollow.PageWatch`).
public enum MightyStructurePan {
    /// Screen points a press moves before it is a drag.
    public static let clickSlop: CGFloat = 3
    /// After a release the page is still the user's for this long: the page's report
    /// of the last drag step may arrive after the mouse is up.
    public static let releaseGrace: TimeInterval = 0.25

    /// What a press lands on.
    public enum Target: Equatable, Sendable {
        /// The page: its background, the gaps between columns and cards, a card's
        /// header and padding, or a text that cannot be selected.
        case page
        /// Selectable text or a native control such as a scroller: it takes its own drag.
        case textOrControl
        /// A scroll view inside the page, such as a code block's.
        case innerScroll
        /// Not this page: another pane, the composer, chrome laid over the page.
        case outside
    }

    /// Whether a left press may start a pan: on the page itself, with no modifier
    /// key (⌃ makes it a right click, ⇧ and ⌘ extend a selection, ⌥ is the system's),
    /// on a page that can move at all. A page that cannot move keeps every press.
    public static func mayPan(_ target: Target, modified: Bool, scrollable: Bool) -> Bool {
        target == .page && !modified && scrollable
    }

    /// The press has moved far enough from where it went down to be a drag.
    public static func isDrag(from start: CGPoint, to point: CGPoint) -> Bool {
        max(abs(point.x - start.x), abs(point.y - start.y)) >= clickSlop
    }

    /// The page can move along either axis between these origins.
    public static func scrollable(minimum: CGPoint, maximum: CGPoint) -> Bool {
        maximum.x - minimum.x > 0.5 || maximum.y - minimum.y > 0.5
    }

    /// The page's origin (top-down, as the page scrolls) for the pointer at `cursor`
    /// after it went down at `press` with the page at `start`: the grabbed point stays
    /// under the pointer, clamped to the page's edges. An axis that cannot move keeps
    /// the origin it had.
    public static func origin(start: CGPoint, press: CGPoint, cursor: CGPoint, minimum: CGPoint, maximum: CGPoint) -> CGPoint {
        CGPoint(x: clamp(start.x - (cursor.x - press.x), minimum.x, maximum.x, keeping: start.x),
                y: clamp(start.y - (cursor.y - press.y), minimum.y, maximum.y, keeping: start.y))
    }

    private static func clamp(_ value: CGFloat, _ low: CGFloat, _ high: CGFloat, keeping current: CGFloat) -> CGFloat {
        guard value.isFinite, low.isFinite, high.isFinite, low <= high else { return current }
        return min(max(value, low), high)
    }
}
