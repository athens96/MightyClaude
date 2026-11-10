import AppKit
import MightyCore
import SwiftUI

/// Drag-to-pan for the 구조 view (`MightyStructurePan`). Placed behind the board's
/// SwiftUI `ScrollView`, the page that scrolls sideways, it finds the native scroll view
/// SwiftUI draws it with and moves that scroll view's clip, and the clip of the column
/// pressed (`MightyStructureColumnScrolling`), the way a wheel scroll moves them, so the
/// columns, their scroll bars and their reports all see an ordinary scroll.
///
/// A local event monitor, as the diagram's camera probe uses, rather than a SwiftUI
/// drag gesture: a gesture on the page would also take the drags that select text,
/// and AppKit's own hit test is what knows a selectable text (a native text field
/// inside SwiftUI's hosting) from the page around it.
///
/// The same monitor scrolls the columns and the page by wheel and trackpad
/// (`MightyStructureWheel`): left to SwiftUI, the pane dock's scroll view around every
/// pane takes them.
struct MightyStructurePanning: NSViewRepresentable {
    let sessionID: String
    let memory: MightyStructureMemory

    func makeNSView(context: Context) -> MightyStructurePanProbe { MightyStructurePanProbe() }
    func updateNSView(_ probe: MightyStructurePanProbe, context: Context) {
        probe.memory = memory
        probe.setAccessibilityIdentifier("mighty-structure-pan-" + sessionID)
        probe.installMonitorIfNeeded()
    }
    static func dismantleNSView(_ probe: MightyStructurePanProbe, coordinator: ()) { probe.dispose() }
}

/// Marks a column's SwiftUI `ScrollView` for the page's probe: placed behind it, its
/// native view finds the native scroll view the column is drawn with, and the memory
/// keeps it (`MightyStructureMemory.columnProbes`).
struct MightyStructureColumnScrolling: NSViewRepresentable {
    let sessionID: String
    let columnID: String
    let memory: MightyStructureMemory

    func makeNSView(context: Context) -> MightyStructureColumnProbe { MightyStructureColumnProbe() }
    func updateNSView(_ probe: MightyStructureColumnProbe, context: Context) {
        probe.columnID = columnID
        probe.setAccessibilityIdentifier("mighty-structure-column-scroll-\(sessionID)-\(columnID)")
        memory.columnProbes.add(probe)
    }
}

@MainActor
final class MightyStructureColumnProbe: NSView {
    var columnID = ""
    private weak var cachedScroll: NSScrollView?
    override var isFlipped: Bool { true }
    override func hitTest(_ point: NSPoint) -> NSView? { nil }
    /// The column's native scroll view: the one with this probe's frame.
    var scrollView: NSScrollView? {
        let found = MightyStructurePanProbe.scrollView(behind: self, cached: cachedScroll)
        cachedScroll = found
        return found
    }
}

@MainActor
final class MightyStructurePanProbe: NSView {
    var memory: MightyStructureMemory?
    /// Where a drag starts: the page's origin, and the column pressed with its origin.
    private struct Start {
        var page: CGPoint
        weak var column: NSScrollView?
        var columnID: String?
        var columnOrigin: CGPoint
    }
    /// A press on the page that has not moved `MightyStructurePan.clickSlop` yet, then
    /// the drag it became. Points are the window's.
    private enum Press {
        case pressed(down: NSEvent, at: CGPoint, start: Start)
        case panning(at: CGPoint, start: Start)
    }
    private var press: Press?
    /// A click on the page that was held back while it might have become a drag,
    /// sent on to what was pressed; these events pass the monitor once.
    private var replays: [(type: NSEvent.EventType, timestamp: TimeInterval)] = []
    private weak var cachedScroll: NSScrollView?
    private var monitor: Any?
    private var resignObserver: NSObjectProtocol?
    private var disposed = false
    /// For the GUI diagnostics: drags that panned, clicks sent on, and what the last press landed on.
    private(set) var pans = 0
    private(set) var replayedClicks = 0
    private(set) var lastTarget: MightyStructurePan.Target?
    private(set) weak var lastHitView: NSView?
    /// For the GUI diagnostics: the column the last press or wheel gesture was over.
    private(set) var lastColumn: String?
    /// For the GUI diagnostics: wheel events that moved the page, a column, and a scroll
    /// view inside a card.
    private(set) var pageWheels = 0
    private(set) var columnWheels = 0
    private(set) var innerWheels = 0
    /// The scroll view the current wheel gesture moves (`MightyStructureWheel.Tracker`).
    private weak var wheelTaker: NSScrollView?
    /// The column the current wheel gesture is over, and its scroll view.
    private var wheelColumnID: String?
    private weak var wheelColumnScroll: NSScrollView?
    var isPanning: Bool { if case .panning = press { true } else { false } }

    override var isFlipped: Bool { true }
    override func hitTest(_ point: NSPoint) -> NSView? { nil }
    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        removeMonitoring()
        if window != nil { installMonitorIfNeeded() }
    }

    func installMonitorIfNeeded() {
        guard !disposed, monitor == nil, let window else { return }
        monitor = NSEvent.addLocalMonitorForEvents(matching: [.leftMouseDown, .leftMouseDragged, .leftMouseUp, .scrollWheel]) { [weak self] event in
            guard let self else { return event }
            return event.type == .scrollWheel ? self.wheel(event) : self.handle(event)
        }
        resignObserver = NotificationCenter.default.addObserver(forName: NSWindow.didResignKeyNotification, object: window, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.cancel() }
        }
    }
    private func removeMonitoring() {
        if let monitor { NSEvent.removeMonitor(monitor); self.monitor = nil }
        if let resignObserver { NotificationCenter.default.removeObserver(resignObserver); self.resignObserver = nil }
        cancel()
    }
    func dispose() { disposed = true; removeMonitoring(); memory = nil }

    private func cancel() {
        if isPanning { NSCursor.pop(); endPan() }
        press = nil
    }
    private func endPan() {
        memory?.panning = false
        memory?.panReleasedAt = ProcessInfo.processInfo.systemUptime
    }

    /// The SwiftUI scroll view this probe sits behind: the native scroll view with the
    /// probe's own frame.
    var scrollView: NSScrollView? {
        let found = Self.scrollView(behind: self, cached: cachedScroll)
        cachedScroll = found
        return found
    }

    /// The native scroll view with `probe`'s frame, near it in the view tree: the one a
    /// SwiftUI `ScrollView` is drawn with, when the probe is its background.
    static func scrollView(behind probe: NSView, cached: NSScrollView?) -> NSScrollView? {
        guard let window = probe.window else { return nil }
        let frame = probe.convert(probe.bounds, to: nil)
        func matches(_ scroll: NSScrollView) -> Bool {
            let other = scroll.convert(scroll.bounds, to: nil)
            return scroll.window === window && abs(other.minX - frame.minX) < 2 && abs(other.minY - frame.minY) < 2
                && abs(other.width - frame.width) < 2 && abs(other.height - frame.height) < 2
        }
        if let cached, matches(cached) { return cached }
        func find(_ view: NSView, depth: Int) -> NSScrollView? {
            guard depth < 24 else { return nil }
            if let scroll = view as? NSScrollView, matches(scroll) { return scroll }
            for child in view.subviews { if let found = find(child, depth: depth + 1) { return found } }
            return nil
        }
        var ancestor = probe.superview
        for _ in 0..<8 {
            guard let current = ancestor else { break }
            if let found = find(current, depth: 0) { return found }
            ancestor = current.superview
        }
        return nil
    }

    /// The board's columns in `page`, each with its native scroll view.
    func columns(in page: NSScrollView) -> [(id: String, scroll: NSScrollView)] {
        (memory?.columnProbes.allObjects ?? []).compactMap { probe in
            guard let scroll = probe.scrollView, scroll.isDescendant(of: page) else { return nil }
            return (probe.columnID, scroll)
        }
    }

    /// The column a press or a wheel at `windowPoint` belongs to: the one whose scroll
    /// view holds the hit view, else the one whose width spans the point (its header).
    func column(at windowPoint: CGPoint, hit: NSView?, in page: NSScrollView) -> (id: String, scroll: NSScrollView)? {
        let all = columns(in: page)
        if let hit, let found = all.first(where: { hit === $0.scroll || hit.isDescendant(of: $0.scroll) }) { return found }
        return all.first { column in
            let frame = column.scroll.convert(column.scroll.bounds, to: nil)
            return windowPoint.x >= frame.minX && windowPoint.x < frame.maxX
        }
    }

    /// The view AppKit sends a press at `windowPoint` to: the same hit test the window
    /// dispatches a mouse-down with.
    func hitView(at windowPoint: CGPoint) -> NSView? {
        guard let content = window?.contentView else { return nil }
        return content.hitTest(content.superview?.convert(windowPoint, from: nil) ?? windowPoint)
    }

    /// A hit view's class and frame in the scroll view's points, for the GUI diagnostics.
    func describe(_ view: NSView?, in scroll: NSScrollView) -> String {
        guard let view else { return "none" }
        return String(describing: type(of: view)) + " " + NSStringFromRect(scroll.convert(view.bounds, from: view))
    }

    /// What a press at `windowPoint` lands on, from AppKit's own hit test. Only the hit
    /// view and its ancestors count: a text view elsewhere being first responder does not.
    /// A text field or text view counts only where its laid-out text is, so the empty
    /// width of a text's frame pans like the card around it. A column's own scroll view
    /// is the page's: only a scroll view inside a card is an inner one.
    func target(at windowPoint: CGPoint, in scroll: NSScrollView) -> MightyStructurePan.Target {
        target(of: hitView(at: windowPoint), at: windowPoint, in: scroll)
    }
    private func target(of hit: NSView?, at windowPoint: CGPoint, in scroll: NSScrollView) -> MightyStructurePan.Target {
        guard scroll.visibleRect.contains(scroll.convert(windowPoint, from: nil)),
              let hit, hit === scroll || hit.isDescendant(of: scroll) else { return .outside }
        let columnScrolls = columns(in: scroll).map(\.scroll)
        var view: NSView? = hit
        while let current = view, current !== scroll {
            if let text = current as? NSTextView { return Self.text(text, contains: windowPoint) ? .textOrControl : .page }
            if let field = current as? NSTextField { return Self.text(field, contains: windowPoint) ? .textOrControl : .page }
            if current is NSControl { return .textOrControl }
            if current is NSScrollView, !columnScrolls.contains(where: { $0 === current }) { return .innerScroll }
            view = current.superview
        }
        return .page
    }

    /// Points around a text's laid-out lines that still press the text.
    private static let textSlop: CGFloat = 4

    /// The point is on a text field's text, as wrapped to its width and cut to its height.
    private static func text(_ field: NSTextField, contains windowPoint: CGPoint) -> Bool {
        let point = field.convert(windowPoint, from: nil)
        let inset = textSlop / 2
        let size = field.attributedStringValue.boundingRect(with: CGSize(width: max(1, field.bounds.width - 2 * inset), height: .greatestFiniteMagnitude),
                                                            options: [.usesLineFragmentOrigin, .usesFontLeading]).size
        let height = min(ceil(size.height), field.bounds.height)
        let rect = CGRect(x: inset, y: field.isFlipped ? 0 : field.bounds.height - height, width: ceil(size.width), height: height)
        return field.visibleRect.contains(point) && rect.insetBy(dx: -textSlop, dy: -textSlop).contains(point)
    }

    /// The point is on a text view's laid-out text. TextKit 2 is asked first: reading a
    /// TextKit 2 view's layout manager would switch it to TextKit 1.
    private static func text(_ view: NSTextView, contains windowPoint: CGPoint) -> Bool {
        let point = view.convert(windowPoint, from: nil)
        var used: CGRect
        if let layout = view.textLayoutManager { used = layout.usageBoundsForTextContainer }
        else if let manager = view.layoutManager, let container = view.textContainer { used = manager.usedRect(for: container) }
        else { return view.visibleRect.contains(point) }
        used.origin.x += view.textContainerOrigin.x
        used.origin.y += view.textContainerOrigin.y
        return view.visibleRect.contains(point) && used.insetBy(dx: -textSlop, dy: -textSlop).contains(point)
    }

    /// The origins the clip may take, as the clip itself constrains them.
    static func originRange(_ clip: NSClipView) -> (minimum: CGPoint, maximum: CGPoint) {
        let size = clip.bounds.size
        let far: CGFloat = 1e7
        return (clip.constrainBoundsRect(NSRect(origin: CGPoint(x: -far, y: -far), size: size)).origin,
                clip.constrainBoundsRect(NSRect(origin: CGPoint(x: far, y: far), size: size)).origin)
    }

    /// The origins a board's scroll view may take: the page moves sideways only, a
    /// column up and down only (`MightyStructurePan.confined`), a scroll view inside a
    /// card either way.
    func range(_ target: NSScrollView, page: NSScrollView, column: NSScrollView?) -> (minimum: CGPoint, maximum: CGPoint) {
        let range = Self.originRange(target.contentView)
        guard target === page || target === column else { return range }
        return MightyStructurePan.confined(minimum: range.minimum, maximum: range.maximum, current: target.contentView.bounds.origin,
                                           to: target === page ? .horizontal : .vertical)
    }

    /// A window point as a clip scrolls: y counted top-down when the clip counts top-down.
    /// Window points hold still while the page and the columns move under them.
    private static func pointer(_ windowPoint: CGPoint, for scroll: NSScrollView) -> CGPoint {
        CGPoint(x: windowPoint.x, y: scroll.contentView.isFlipped ? -windowPoint.y : windowPoint.y)
    }

    private func handle(_ event: NSEvent) -> NSEvent? {
        if let index = replays.firstIndex(where: { $0.type == event.type && $0.timestamp == event.timestamp }) {
            replays.remove(at: index)
            return event
        }
        guard !disposed, let window, window.attachedSheet == nil, !isHiddenOrHasHiddenAncestor else { cancel(); return event }
        // The first click in an inactive window is AppKit's, to make it key.
        guard NSApp.isActive, window.isKeyWindow else { cancel(); return event }
        if let press {
            guard event.type != .leftMouseDown else { cancel(); return event }
            let point = event.window === window ? event.locationInWindow
                : window.convertPoint(fromScreen: event.window?.convertPoint(toScreen: event.locationInWindow) ?? event.locationInWindow)
            guard let scroll = scrollView else { cancel(); return event }
            switch press {
            case .pressed(let down, let at, let start):
                if event.type == .leftMouseUp {
                    // A click: it goes to what was pressed, a fold toggle or a reveal button.
                    self.press = nil
                    replay(down, event)
                    return nil
                }
                guard MightyStructurePan.isDrag(from: at, to: point) else { return nil }
                self.press = .panning(at: at, start: start)
                pans += 1
                memory?.panning = true
                memory?.movingColumn = start.columnID
                NSCursor.closedHand.push()
                pan(scroll, at: at, start: start, to: point)
            case .panning(let at, let start):
                pan(scroll, at: at, start: start, to: point)
                if event.type == .leftMouseUp {
                    self.press = nil
                    NSCursor.pop()
                    endPan()
                }
            }
            return nil
        }
        guard event.type == .leftMouseDown, event.window === window, let scroll = scrollView else { return event }
        let hit = hitView(at: event.locationInWindow)
        let target = target(of: hit, at: event.locationInWindow, in: scroll)
        lastTarget = target
        lastHitView = hit
        let column = target == .page ? column(at: event.locationInWindow, hit: hit, in: scroll) : nil
        lastColumn = column?.id
        let modified = !event.modifierFlags.intersection([.control, .option, .shift, .command]).isEmpty
        let scrollable = [scroll, column?.scroll].compactMap { $0 }.contains { candidate in
            let range = range(candidate, page: scroll, column: column?.scroll)
            return MightyStructurePan.scrollable(minimum: range.minimum, maximum: range.maximum)
        }
        guard MightyStructurePan.mayPan(target, modified: modified, scrollable: scrollable) else { return event }
        press = .pressed(down: event, at: event.locationInWindow,
                         start: Start(page: scroll.contentView.bounds.origin, column: column?.scroll, columnID: column?.id,
                                      columnOrigin: column?.scroll.contentView.bounds.origin ?? .zero))
        return nil
    }

    /// A wheel or trackpad event. One that lands on the page, and the rest of a gesture
    /// that began there, moves the scroll view under the pointer that can still move that
    /// way, and goes no further: a scroll view inside a card, the column (up and down), or
    /// the page (sideways). Like any scroll view, an inactive window scrolls too.
    private func wheel(_ event: NSEvent) -> NSEvent? {
        let stage = MightyStructureWheel.stage(phase: event.phase.rawValue, momentumPhase: event.momentumPhase.rawValue)
        guard !disposed, let window, event.window === window, window.attachedSheet == nil, !isHiddenOrHasHiddenAncestor,
              let memory, let scroll = scrollView else {
            // A gesture beginning anywhere else ends any this page still holds open.
            if stage == .began, let memory { _ = memory.wheel.feed(.began, overPage: false, delta: .zero, at: ProcessInfo.processInfo.systemUptime) }
            return event
        }
        let hit = stage == .step || stage == .began ? hitView(at: event.locationInWindow) : nil
        let overPage = hit.map { target(of: $0, at: event.locationInWindow, in: scroll) != .outside } ?? false
        // A mouse wheel without precise deltas counts lines, as the scroll view it moves
        // counts them: the page's sideways, the column's up and down.
        let precise = event.hasPreciseScrollingDeltas
        let lines = precise ? nil : (column(at: event.locationInWindow, hit: hit ?? hitView(at: event.locationInWindow), in: scroll)?.scroll ?? scroll)
        let delta = CGPoint(x: event.scrollingDeltaX * (lines == nil ? 1 : scroll.horizontalLineScroll),
                            y: event.scrollingDeltaY * (lines?.verticalLineScroll ?? 1))
        switch memory.wheel.feed(stage, overPage: overPage, delta: delta, at: ProcessInfo.processInfo.systemUptime) {
        case .pass: return event
        case .hold: return nil
        case .choose:
            // Innermost first, then the column, the page last: the first that can still
            // move this way takes it.
            let hitNow = hit ?? hitView(at: event.locationInWindow)
            let column = column(at: event.locationInWindow, hit: hitNow, in: scroll)
            var candidates: [NSScrollView] = []
            var view = hitNow
            while let current = view, current !== scroll {
                if let inner = current as? NSScrollView, inner !== column?.scroll, current.isDescendant(of: scroll) { candidates.append(inner) }
                view = current.superview
            }
            if let column { candidates.append(column.scroll) }
            candidates.append(scroll)
            let axis = memory.wheel.axis
            let taker = candidates[MightyStructureWheel.taker(canMove: candidates.map { candidate in
                let range = range(candidate, page: scroll, column: column?.scroll)
                return MightyStructureWheel.canMove(current: candidate.contentView.bounds.origin, delta: Self.topDown(delta, in: candidate),
                                                    axis: axis, minimum: range.minimum, maximum: range.maximum)
            })]
            wheelTaker = taker
            wheelColumnID = column?.id
            wheelColumnScroll = column?.scroll
            lastColumn = column?.id
            memory.movingColumn = taker === column?.scroll ? column?.id : nil
            move(taker, by: delta, axis: axis, page: scroll)
        case .move:
            move(wheelTaker ?? scroll, by: delta, axis: memory.wheel.axis, page: scroll)
        }
        return nil
    }

    /// A wheel movement in a scroll view's top-down origin space.
    private static func topDown(_ delta: CGPoint, in scroll: NSScrollView) -> CGPoint {
        scroll.contentView.isFlipped ? delta : CGPoint(x: delta.x, y: -delta.y)
    }

    private func move(_ target: NSScrollView, by delta: CGPoint, axis: MightyStructureWheel.Axis?, page: NSScrollView) {
        let column = target === wheelColumnScroll ? wheelColumnID : nil
        if target === page { pageWheels += 1 } else if column != nil { columnWheels += 1 } else { innerWheels += 1 }
        let clip = target.contentView
        let range = range(target, page: page, column: column == nil ? nil : target)
        let next = MightyStructureWheel.origin(current: clip.bounds.origin, delta: Self.topDown(delta, in: target), axis: axis,
                                               minimum: range.minimum, maximum: range.maximum)
        guard next != clip.bounds.origin else { return }
        let previous = clip.bounds.origin
        clip.scroll(to: next)
        target.reflectScrolledClipView(clip)
        if let column { judgeFollow(column, target, from: previous) }
    }

    /// The user moved a column: the follow rule judges it now (`ColumnWatch.userScrolled`),
    /// not when the column's report of it arrives.
    private func judgeFollow(_ id: String, _ scroll: NSScrollView, from previous: CGPoint) {
        let moved = scroll.contentView.bounds.origin.y - previous.y
        memory?.columns.userScrolled(id, byY: Double(scroll.contentView.isFlipped ? moved : -moved))
    }

    /// A drag step: the grabbed point stays under the pointer, the page following it
    /// sideways and the column pressed up and down.
    private func pan(_ page: NSScrollView, at: CGPoint, start: Start, to point: CGPoint) {
        for (scroll, origin) in [(page, start.page), (start.column, start.columnOrigin)] {
            guard let scroll else { continue }
            let clip = scroll.contentView
            let range = range(scroll, page: page, column: start.column)
            let next = MightyStructurePan.origin(start: origin, press: Self.pointer(at, for: scroll), cursor: Self.pointer(point, for: scroll),
                                                 minimum: range.minimum, maximum: range.maximum)
            guard next != clip.bounds.origin else { continue }
            let previous = clip.bounds.origin
            clip.scroll(to: next)
            scroll.reflectScrolledClipView(clip)
            if scroll === start.column, let id = start.columnID { judgeFollow(id, scroll, from: previous) }
        }
    }

    private func replay(_ down: NSEvent, _ up: NSEvent) {
        replayedClicks += 1
        replays = [(down.type, down.timestamp), (up.type, up.timestamp)]
        NSApp.postEvent(down, atStart: false)
        NSApp.postEvent(up, atStart: false)
    }
}
