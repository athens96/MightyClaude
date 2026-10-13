import CoreGraphics
import Foundation

/// Follow mode for the Mac's Mighty view, its diagram and its 구조 view. While the
/// reader is at the newest work, the newest block's bottom sits right above the
/// composer: a new block or a block that settles to a new height is shown as it
/// arrives. A reader who scrolls, pans or zooms away is left exactly where they
/// are until they come back to the newest work or send a request.
///
/// Mac-only. The shared camera rules (`MightyGraphCamera`, checked against
/// `native/contracts/graph-vectors.json` on Windows too) are left as they are; the
/// Mac diagram stops calling the ones these replace: the top-aligned aim at a new
/// request and the re-aim when the canvas changes size.
public enum MightyGraphFollow {
    /// Screen points between the followed block's bottom and the composer: the
    /// gap a revealed result card always had (`MightyGraphLayout.revealOffset`).
    public static let margin: CGFloat = 16
    /// How far the followed block's bottom may hide behind the composer and the
    /// reader still counts as at the newest work (`TranscriptViewport.followSlack`).
    public static let slack = CGFloat(TranscriptViewport.followSlack)

    // MARK: Diagram

    /// The block a following camera keeps above the composer: the newest request's
    /// lowest block in the flow — its result, a plan waiting for an answer, its
    /// deepest sub-agent, its 실행 block or the request itself — and of blocks in one
    /// row the one added last. Blocks hung beside the flow and the next-request
    /// draft never count, so typing a draft never moves the view; with no request
    /// the draft block is the only block there is.
    public static func targetID(_ layout: MightyGraphLayout, runs: [MightyGraphRun]) -> String? {
        guard !runs.isEmpty else { return layout.nodes.first { $0.content == .draft }?.id }
        let newest = runs.count - 1
        var best: MightyGraphLayout.Node?
        for node in layout.nodes where run(of: node.content) == newest {
            if node.frame.maxY >= (best?.frame.maxY ?? -.infinity) { best = node }
        }
        return best?.id
    }

    private static func run(of content: MightyGraphLayout.Content) -> Int? {
        switch content {
        case .request(let index), .work(let index), .result(let index), .plan(let index), .agent(let index, _): index
        default: nil
        }
    }

    /// The camera shows the newest work: the followed block's bottom is on screen,
    /// or hidden behind the composer by no more than `slack`.
    public static func isAtNewest(target: CGRect, camera: CGPoint, viewport: CGSize, zoom: CGFloat) -> Bool {
        guard valid(camera: camera, viewport: viewport, zoom: zoom) else { return false }
        let bottom = camera.y + target.maxY * zoom
        return bottom >= 0 && bottom <= viewport.height - margin + slack
    }

    /// The camera x that puts the diagram's centreline, which every request is
    /// on, in the middle of the viewport.
    public static func centredX(viewport: CGSize, zoom: CGFloat) -> CGFloat { viewport.width / 2 - MightyGraphCamera.centreX * zoom }

    /// The camera that puts `target`'s bottom `gap` points above the viewport's
    /// bottom, at camera x `x`. A block taller than the viewport shows its bottom,
    /// where its newest content is.
    public static func camera(bottomOf target: CGRect, viewport: CGSize, zoom: CGFloat, gap: CGFloat = margin, x: CGFloat) -> CGPoint {
        CGPoint(x: x, y: viewport.height - gap - target.maxY * zoom)
    }

    /// A camera target's offset. `follows` (the first placement, a send) puts the
    /// block's bottom right above the composer on the centreline; anything else is
    /// placed by the shared rule.
    public static func cameraOffset(for frame: CGRect, viewport: CGSize, zoom: CGFloat, alignTop: Bool, alignBottom: Bool, follows: Bool) -> CGPoint {
        guard follows else { return MightyGraphLayout.cameraOffset(for: frame, viewport: viewport, zoom: zoom, alignTop: alignTop, alignBottom: alignBottom) }
        return camera(bottomOf: frame, viewport: viewport, zoom: zoom, x: centredX(viewport: viewport, zoom: zoom))
    }

    private static func valid(camera: CGPoint, viewport: CGSize, zoom: CGFloat) -> Bool {
        zoom.isFinite && zoom > 0 && camera.x.isFinite && camera.y.isFinite
            && viewport.width > 0 && viewport.height > 0 && viewport.width.isFinite && viewport.height.isFinite
    }

    /// The diagram camera's follow state between layout passes.
    public struct Watch: Equatable, Sendable {
        public private(set) var following = true
        /// The block the last pass followed, and its bottom's distance above the
        /// viewport's bottom then.
        private var targetID: String?
        private var gap: CGFloat?
        /// The user changed the layout themselves; the next pass judges where that left them.
        private var judging = false
        public init() {}

        /// A send or the first placement: follow, with the newest block right above the composer.
        public mutating func start() { following = true; targetID = nil; gap = nil; judging = false }
        /// The app aimed somewhere the user asked for, or the user zoomed: nothing
        /// follows until the reader comes back to the newest work or sends a request.
        public mutating func stop() { following = false; targetID = nil; gap = nil; judging = false }
        /// The user opened, folded or resized a block. Nothing moves for it: the next
        /// pass follows on only if the newest block's bottom is still in view, from
        /// where it is now.
        public mutating func userChangedLayout() { if following { judging = true } }
        /// The user moved the camera, or an aim landed: follow only when the
        /// camera shows the newest work, keeping the block where it is now.
        public mutating func moved(targetID: String?, target: CGRect?, camera: CGPoint, viewport: CGSize, zoom: CGFloat) {
            guard let targetID, let target, MightyGraphFollow.isAtNewest(target: target, camera: camera, viewport: viewport, zoom: zoom) else {
                stop(); return
            }
            following = true
            self.targetID = targetID
            gap = viewport.height - (camera.y + target.maxY * zoom)
        }
        /// A layout pass, a new followed block or a new viewport size. Returns the
        /// camera to move to, nil to stay. A new block is put right above the
        /// composer; the same block keeps its distance from the composer, so a
        /// block settling to its height, a trim above it or a taller composer never
        /// takes it off screen. Only y moves: the reader's x is theirs.
        public mutating func observe(targetID: String?, target: CGRect?, camera: CGPoint, viewport: CGSize, zoom: CGFloat) -> CGPoint? {
            guard following, let targetID, let target, MightyGraphFollow.valid(camera: camera, viewport: viewport, zoom: zoom) else { return nil }
            if judging {
                judging = false
                moved(targetID: targetID, target: target, camera: camera, viewport: viewport, zoom: zoom)
                return nil
            }
            let gap = targetID == self.targetID ? (self.gap ?? margin) : margin
            self.targetID = targetID
            self.gap = gap
            let next = MightyGraphFollow.camera(bottomOf: target, viewport: viewport, zoom: zoom, gap: gap, x: camera.x)
            return abs(next.y - camera.y) > 0.5 ? next : nil
        }
    }

    // MARK: 구조 view

    /// Where the 구조 view's page stands, in its own top-down coordinates.
    public struct Page: Equatable, Sendable {
        public var originY: Double
        public var visibleHeight: Double
        public var documentHeight: Double
        /// The newest request, and its card's bottom when the card is laid out.
        public var newestID: String?
        public var newestBottom: Double?
        public init(originY: Double, visibleHeight: Double, documentHeight: Double, newestID: String?, newestBottom: Double?) {
            self.originY = originY; self.visibleHeight = visibleHeight; self.documentHeight = documentHeight
            self.newestID = newestID; self.newestBottom = newestBottom
        }
        /// The newest card's bottom is on screen or hidden by no more than the
        /// slack; with no request, the page's end is.
        var atNewest: Bool {
            guard newestID != nil else {
                return TranscriptViewport.isFollowing(originY: originY, visibleHeight: visibleHeight, documentHeight: documentHeight)
            }
            guard let newestBottom else { return false }
            return newestBottom >= originY && newestBottom <= originY + visibleHeight + Double(slack)
        }
        /// The newest card's bottom is below the viewport or above it.
        var newestHidden: Bool {
            guard let newestBottom else { return false }
            return newestBottom > originY + visibleHeight + 0.5 || newestBottom < originY
        }
        func moved(from last: Page) -> Bool { abs(originY - last.originY) > 0.5 }
        func laidOut(from last: Page) -> Bool {
            newestID != last.newestID || abs(documentHeight - last.documentHeight) > 0.5 || abs(visibleHeight - last.visibleHeight) > 0.5
                || (newestBottom == nil) != (last.newestBottom == nil) || abs((newestBottom ?? 0) - (last.newestBottom ?? 0)) > 0.5
        }
    }

    /// Where the 구조 view scrolls: the page's end, or the newest card's bottom
    /// when more of the page (a taller column) goes on below it.
    public enum Scroll: Equatable, Sendable {
        case bottom
        case card(String)
    }

    /// The 구조 view's follow state between page reports.
    public struct PageWatch: Equatable, Sendable {
        public private(set) var following = true
        private var last: Page?
        /// A new request being brought into view: the lazy page may land short of
        /// it, so its own scrolls do not decide the follow state until it shows.
        private var showing: String?
        private var tries = 0
        private static let showTries = 3
        /// The page came back without following: its first report says where the reader is.
        private var judging = false
        public init() {}

        /// The user opened or folded a text: they are reading, nothing follows.
        public mutating func stop() { following = false; showing = nil; judging = false }
        /// The view appeared again: a following reader is shown the newest card; any
        /// other reader follows from the first report if the page opened at the newest card.
        public mutating func appeared(newestID: String?) -> Scroll? {
            last = nil
            guard following else { judging = true; return nil }
            guard let newestID else { return .bottom }
            showing = newestID; tries = Self.showTries
            return .card(newestID)
        }
        /// A new request is the user's own: shown, and followed from now on.
        public mutating func newRequest(_ id: String) -> Scroll {
            following = true
            judging = false
            showing = id; tries = Self.showTries
            return .card(id)
        }
        /// A page report. `userScrolling` is a scroll the user's fingers or wheel
        /// are making right now, when the system says so. Returns where to scroll.
        ///
        /// The page only moving is a scroll: the reader follows when they are at
        /// the newest card. The page laid out anew (a card grew, a new card, a
        /// taller composer) keeps the follow state it had before, so a growth that
        /// hides the newest card in one pass is still followed; and it scrolls only
        /// when the newest card's bottom is hidden, never up to it from below, so a
        /// reader at the true end of the page stays there.
        public mutating func observe(_ page: Page, userScrolling: Bool) -> Scroll? {
            let previous = last
            last = page
            if judging {
                judging = false
                following = page.atNewest
                return nil
            }
            if userScrolling {
                showing = nil
                following = page.atNewest
                return nil
            }
            if let previous, page.moved(from: previous), !page.laidOut(from: previous), showing == nil {
                following = page.atNewest
                return nil
            }
            guard following else { return nil }
            if let showing, page.newestID == showing, page.newestBottom != nil, !page.newestHidden { self.showing = nil }
            guard page.newestHidden, let id = page.newestID, let bottom = page.newestBottom else { return nil }
            if showing != nil {
                tries -= 1
                if tries <= 0 { showing = nil }
            }
            return page.documentHeight - bottom <= Double(slack) ? .bottom : .card(id)
        }
    }
}
