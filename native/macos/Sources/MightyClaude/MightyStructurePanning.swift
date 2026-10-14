import AppKit
import MightyCore
import SwiftUI

/// Drag-to-pan for the 구조 view (`MightyStructurePan`). Placed behind the view's
/// SwiftUI `ScrollView`, it finds the native scroll view SwiftUI draws it with and
/// moves that scroll view's clip, the way a wheel scroll moves it, so the lazy page,
/// its scroll bars and its page reports all see an ordinary scroll.
///
/// A local event monitor, as the diagram's camera probe uses, rather than a SwiftUI
/// drag gesture: a gesture on the page would also take the drags that select text,
/// and AppKit's own hit test is what knows a selectable text (a native text field
/// inside SwiftUI's hosting) from the page around it.
///
/// The same monitor scrolls the page by wheel and trackpad (`MightyStructureWheel`):
/// left to SwiftUI, the pane dock's scroll view around every pane takes them.
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

@MainActor
final class MightyStructurePanProbe: NSView {
    var memory: MightyStructureMemory?
    /// A press on the page that has not moved `MightyStructurePan.clickSlop` yet, then
    /// the drag it became. Points are the scroll view's, top-down.
    private enum Press {
        case pressed(down: NSEvent, at: CGPoint, origin: CGPoint)
        case panning(at: CGPoint, origin: CGPoint)
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
    /// For the GUI diagnostics: wheel events that moved the page, and ones that moved
    /// a scroll view inside it.
    private(set) var pageWheels = 0
    private(set) var innerWheels = 0
    /// The scroll view the current wheel gesture moves (`MightyStructureWheel.Tracker`).
    private weak var wheelTaker: NSScrollView?
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
        guard let window else { return nil }
        let frame = convert(bounds, to: nil)
        func matches(_ scroll: NSScrollView) -> Bool {
            let other = scroll.convert(scroll.bounds, to: nil)
            return scroll.window === window && abs(other.minX - frame.minX) < 2 && abs(other.minY - frame.minY) < 2
                && abs(other.width - frame.width) < 2 && abs(other.height - frame.height) < 2
        }
        if let cachedScroll, matches(cachedScroll) { return cachedScroll }
        func find(_ view: NSView, depth: Int) -> NSScrollView? {
            guard depth < 24 else { return nil }
            if let scroll = view as? NSScrollView, matches(scroll) { return scroll }
            for child in view.subviews { if let found = find(child, depth: depth + 1) { return found } }
            return nil
        }
        var ancestor = superview
        for _ in 0..<8 {
            guard let current = ancestor else { break }
            if let found = find(current, depth: 0) { cachedScroll = found; return found }
            ancestor = current.superview
        }
        return nil
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
    /// width of a text's frame pans like the card around it.
    func target(at windowPoint: CGPoint, in scroll: NSScrollView) -> MightyStructurePan.Target {
        target(of: hitView(at: windowPoint), at: windowPoint, in: scroll)
    }
    private func target(of hit: NSView?, at windowPoint: CGPoint, in scroll: NSScrollView) -> MightyStructurePan.Target {
        guard scroll.visibleRect.contains(scroll.convert(windowPoint, from: nil)),
              let hit, hit === scroll || hit.isDescendant(of: scroll) else { return .outside }
        var view: NSView? = hit
        while let current = view, current !== scroll {
            if let text = current as? NSTextView { return Self.text(text, contains: windowPoint) ? .textOrControl : .page }
            if let field = current as? NSTextField { return Self.text(field, contains: windowPoint) ? .textOrControl : .page }
            if current is NSControl { return .textOrControl }
            if current is NSScrollView { return .innerScroll }
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

    /// A window point in the scroll view's own space, top-down as the clip scrolls.
    private func pointer(_ windowPoint: CGPoint, in scroll: NSScrollView) -> CGPoint {
        let point = scroll.convert(windowPoint, from: nil)
        let topDown = scroll.isFlipped ? point.y : scroll.bounds.height - point.y
        return CGPoint(x: point.x, y: scroll.contentView.isFlipped ? topDown : -topDown)
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
            let windowPoint = event.window === window ? event.locationInWindow
                : window.convertPoint(fromScreen: event.window?.convertPoint(toScreen: event.locationInWindow) ?? event.locationInWindow)
            guard let scroll = scrollView else { cancel(); return event }
            let point = pointer(windowPoint, in: scroll)
            switch press {
            case .pressed(let down, let at, let origin):
                if event.type == .leftMouseUp {
                    // A click: it goes to what was pressed, a fold toggle or a reveal button.
                    self.press = nil
                    replay(down, event)
                    return nil
                }
                guard MightyStructurePan.isDrag(from: at, to: point) else { return nil }
                self.press = .panning(at: at, origin: origin)
                pans += 1
                memory?.panning = true
                NSCursor.closedHand.push()
                pan(scroll, at: at, origin: origin, to: point)
            case .panning(let at, let origin):
                pan(scroll, at: at, origin: origin, to: point)
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
        let range = Self.originRange(scroll.contentView)
        let modified = !event.modifierFlags.intersection([.control, .option, .shift, .command]).isEmpty
        guard MightyStructurePan.mayPan(target, modified: modified,
                                        scrollable: MightyStructurePan.scrollable(minimum: range.minimum, maximum: range.maximum)) else { return event }
        press = .pressed(down: event, at: pointer(event.locationInWindow, in: scroll), origin: scroll.contentView.bounds.origin)
        return nil
    }

    /// A wheel or trackpad event. One that lands on the page, and the rest of a gesture
    /// that began there, moves the page or the scroll view inside it that can still move
    /// that way, and goes no further. Like any scroll view, an inactive window scrolls too.
    private func wheel(_ event: NSEvent) -> NSEvent? {
        let stage = MightyStructureWheel.stage(phase: event.phase.rawValue, momentumPhase: event.momentumPhase.rawValue)
        guard !disposed, let window, event.window === window, window.attachedSheet == nil, !isHiddenOrHasHiddenAncestor,
              let memory, let scroll = scrollView else {
            // A gesture beginning anywhere else ends any this page still holds open.
            if stage == .began, let memory { _ = memory.wheel.feed(.began, overPage: false, delta: .zero, at: ProcessInfo.processInfo.systemUptime) }
            return event
        }
        // A mouse wheel without precise deltas counts lines, as the scroll view's line scroll moves them.
        let precise = event.hasPreciseScrollingDeltas
        let delta = CGPoint(x: event.scrollingDeltaX * (precise ? 1 : scroll.horizontalLineScroll),
                            y: event.scrollingDeltaY * (precise ? 1 : scroll.verticalLineScroll))
        let hit = stage == .step || stage == .began ? hitView(at: event.locationInWindow) : nil
        let overPage = hit.map { target(of: $0, at: event.locationInWindow, in: scroll) != .outside } ?? false
        switch memory.wheel.feed(stage, overPage: overPage, delta: delta, at: ProcessInfo.processInfo.systemUptime) {
        case .pass: return event
        case .hold: return nil
        case .choose:
            // Innermost first, the page last: the first that can still move this way takes it.
            let hitNow = hit ?? hitView(at: event.locationInWindow)
            var candidates: [NSScrollView] = []
            var view = hitNow
            while let current = view, current !== scroll {
                if let inner = current as? NSScrollView, current.isDescendant(of: scroll) { candidates.append(inner) }
                view = current.superview
            }
            candidates.append(scroll)
            let axis = memory.wheel.axis
            let taker = candidates[MightyStructureWheel.taker(canMove: candidates.map { candidate in
                let range = Self.originRange(candidate.contentView)
                return MightyStructureWheel.canMove(current: candidate.contentView.bounds.origin, delta: Self.topDown(delta, in: candidate),
                                                    axis: axis, minimum: range.minimum, maximum: range.maximum)
            })]
            wheelTaker = taker
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
        if target === page { pageWheels += 1 } else { innerWheels += 1 }
        let clip = target.contentView
        let range = Self.originRange(clip)
        let next = MightyStructureWheel.origin(current: clip.bounds.origin, delta: Self.topDown(delta, in: target), axis: axis,
                                               minimum: range.minimum, maximum: range.maximum)
        guard next != clip.bounds.origin else { return }
        clip.scroll(to: next)
        target.reflectScrolledClipView(clip)
    }

    private func pan(_ scroll: NSScrollView, at: CGPoint, origin: CGPoint, to point: CGPoint) {
        let clip = scroll.contentView
        let range = Self.originRange(clip)
        let next = MightyStructurePan.origin(start: origin, press: at, cursor: point, minimum: range.minimum, maximum: range.maximum)
        guard next != clip.bounds.origin else { return }
        clip.scroll(to: next)
        scroll.reflectScrolledClipView(clip)
    }

    private func replay(_ down: NSEvent, _ up: NSEvent) {
        replayedClicks += 1
        replays = [(down.type, down.timestamp), (up.type, up.timestamp)]
        NSApp.postEvent(down, atStart: false)
        NSApp.postEvent(up, atStart: false)
    }
}
