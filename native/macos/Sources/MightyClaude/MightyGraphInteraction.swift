import AppKit
import SwiftUI

/// The probe stays in the viewport; only the diagram behind it is translated.
struct MightyGraphInteraction<Content: View>: NSViewRepresentable {
    let sessionID: String
    let nodes: [MightyGraphLayout.Node]
    let zoom: CGFloat
    let viewportSize: CGSize
    let targetToken: String?
    let targetFrame: CGRect?
    let alignTop: Bool
    @Binding var selection: String?
    @Binding var panOffset: CGPoint
    let content: Content

    func makeNSView(context: Context) -> MightyGraphViewportHost<Content> {
        MightyGraphViewportHost(content: content)
    }
    func updateNSView(_ host: MightyGraphViewportHost<Content>, context: Context) {
        host.hosting.rootView = content
        host.setAccessibilityIdentifier("mighty-graph-canvas-" + sessionID)
        let view = host.probe
        view.setAccessibilityIdentifier("mighty-graph-interaction-" + sessionID)
        view.sessionID = sessionID
        view.layoutFrames = nodes.map { ($0.id, $0.frame) }
        view.zoom = zoom
        view.expectedViewportSize = viewportSize
        view.panOffset = panOffset
        view.selectedNodeID = selection
        view.targetToken = targetToken
        view.targetFrame = targetFrame
        view.alignTop = alignTop
        view.onSelect = { selection = $0 }
        view.onPan = { panOffset = $0 }
        view.installMonitorIfNeeded()
        view.scheduleInitialPosition()
    }
    static func dismantleNSView(_ view: MightyGraphViewportHost<Content>, coordinator: ()) { view.probe.dispose() }
}

/// A real native subtree makes wheel, keyboard and inner-scroll lookup local
/// to this graph even when another SwiftUI pane or overlay occupies its window.
@MainActor
final class MightyGraphViewportHost<Content: View>: NSView {
    let hosting: NSHostingView<Content>
    let probe = MightyGraphInteractionProbe()
    override var isFlipped: Bool { true }
    init(content: Content) {
        hosting = NSHostingView(rootView: content)
        super.init(frame: .zero)
        hosting.sizingOptions = []
        setAccessibilityElement(true)
        setAccessibilityRole(.group)
        setAccessibilityLabel("마이티 그래프")
        wantsLayer = true; layer?.masksToBounds = true
        addSubview(hosting); addSubview(probe)
        probe.graphRoot = hosting; probe.setAccessibilityElement(false)
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
    override func accessibilityChildren() -> [Any]? { hosting.accessibilityChildren() }
    override func layout() {
        super.layout(); hosting.frame = bounds; probe.frame = bounds
        probe.scheduleInitialPosition()
    }
}

@MainActor
final class MightyGraphInteractionProbe: NSView {
    weak var graphRoot: NSView?
    var sessionID = ""
    var layoutFrames: [(String, CGRect)] = []
    var zoom: CGFloat = 1
    var expectedViewportSize: CGSize = .zero
    var panOffset: CGPoint = .zero
    var selectedNodeID: String?
    var onSelect: (String?) -> Void = { _ in }
    var onPan: (CGPoint) -> Void = { _ in }
    var targetToken: String?
    var targetFrame: CGRect?
    var alignTop = false
    private(set) var consumedTargetToken: String?
    private(set) var forwardedWheels = 0
    private(set) var nativeWheels = 0
    private(set) var isPanning = false
    private var dragLocation: CGPoint?
    private var monitor: Any?
    private var resignObserver: NSObjectProtocol?
    private var closeObserver: NSObjectProtocol?
    private var disposed = false
    private var positioningScheduled = false
    private enum WheelRoute { case canvas, inner }
    private var gestureRoute: WheelRoute?
    private weak var gestureTarget: NSScrollView?
    private var discardedMomentum = false
    var viewportSize: CGSize { bounds.size }
    var frames: [(String, CGRect)] {
        layoutFrames.map { ($0.0, CGRect(x: $0.1.minX * zoom + panOffset.x, y: $0.1.minY * zoom + panOffset.y, width: $0.1.width * zoom, height: $0.1.height * zoom)) }
    }
    var diagnostic: [String: String] {
        ["panOffset": NSStringFromPoint(panOffset), "viewport": NSStringFromRect(bounds),
         "targetToken": targetToken ?? "nil", "consumedTargetToken": consumedTargetToken ?? "nil",
         "selectedNodeID": selectedNodeID ?? "nil", "isPanning": String(isPanning)]
    }
    override var isFlipped: Bool { true }
    override var acceptsFirstResponder: Bool { true }
    override func hitTest(_ point: NSPoint) -> NSView? { nil }
    override func layout() { super.layout(); scheduleInitialPosition() }
    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        removeMonitoring()
        if window != nil { installMonitorIfNeeded(); scheduleInitialPosition() }
    }
    func installMonitorIfNeeded() {
        guard !disposed, monitor == nil, let window else { return }
        monitor = NSEvent.addLocalMonitorForEvents(matching: [.scrollWheel, .leftMouseDown, .leftMouseDragged, .leftMouseUp, .keyDown]) { [weak self] event in
            guard let self else { return event }
            return self.handle(event)
        }
        resignObserver = NotificationCenter.default.addObserver(forName: NSWindow.didResignKeyNotification, object: window, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.cancelInteraction() }
        }
        closeObserver = NotificationCenter.default.addObserver(forName: NSWindow.willCloseNotification, object: window, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.cancelInteraction() }
        }
    }
    private func removeMonitoring() {
        if let monitor { NSEvent.removeMonitor(monitor); self.monitor = nil }
        if let resignObserver { NotificationCenter.default.removeObserver(resignObserver); self.resignObserver = nil }
        if let closeObserver { NotificationCenter.default.removeObserver(closeObserver); self.closeObserver = nil }
        cancelInteraction()
    }
    private func cancelInteraction() {
        if isPanning { NSCursor.pop() }
        isPanning = false; dragLocation = nil
        gestureRoute = nil; gestureTarget = nil; discardedMomentum = false
    }
    func dispose() {
        disposed = true; removeMonitoring(); onSelect = { _ in }; onPan = { _ in }; layoutFrames = []
    }
    func setPanOffset(_ value: CGPoint) {
        guard value.x.isFinite, value.y.isFinite else { return }
        consumedTargetToken = targetToken
        applyPan(value)
    }
    private func applyPan(_ value: CGPoint) {
        guard value.x.isFinite, value.y.isFinite, value != panOffset else { return }
        panOffset = value; onPan(value)
    }
    func scheduleInitialPosition() {
        guard !disposed, !positioningScheduled else { return }
        positioningScheduled = true
        DispatchQueue.main.async { [weak self] in
            guard let self else { return }
            self.positioningScheduled = false
            guard !self.disposed, self.window != nil, let token = self.targetToken, token != self.consumedTargetToken,
                  let frame = self.targetFrame, self.bounds.width > 0, self.bounds.height > 0,
                  abs(self.bounds.width - self.expectedViewportSize.width) < 1,
                  abs(self.bounds.height - self.expectedViewportSize.height) < 1 else { return }
            self.consumedTargetToken = token
            self.applyPan(CGPoint(x: (self.bounds.width - frame.width * self.zoom) / 2 - frame.minX * self.zoom,
                y: (self.alignTop ? 16 : max(16, (self.bounds.height - frame.height * self.zoom) / 2)) - frame.minY * self.zoom))
        }
    }
    private func select(_ id: String?) {
        guard selectedNodeID != id else { return }
        selectedNodeID = id; onSelect(id)
    }
    private func handle(_ event: NSEvent) -> NSEvent? {
        guard !disposed, let window, event.window === window, window.attachedSheet == nil,
              !isHiddenOrHasHiddenAncestor, let graphRoot else { cancelInteraction(); return event }
        if event.type == .keyDown {
            guard event.keyCode == 53 else { return event }
            if isPanning { cancelInteraction(); return nil }
            guard selectedNodeID != nil, let responder = window.firstResponder as? NSView else { return event }
            if let text = responder as? NSTextView, text.hasMarkedText() { return event }
            guard responder === self || responder === graphRoot || responder.isDescendant(of: graphRoot) else { return event }
            select(nil); return nil
        }
        let point = convert(event.locationInWindow, from: nil)
        if isPanning {
            if event.type == .leftMouseDragged, let previous = dragLocation {
                setPanOffset(CGPoint(x: panOffset.x + point.x - previous.x, y: panOffset.y + point.y - previous.y))
                dragLocation = point; return nil
            }
            if event.type == .leftMouseUp { cancelInteraction(); return nil }
        }
        if event.type == .scrollWheel {
            if event.phase.contains(.began) || event.phase.contains(.mayBegin) { gestureRoute = nil; gestureTarget = nil; discardedMomentum = false }
            if !event.momentumPhase.isEmpty, discardedMomentum {
                if event.momentumPhase.contains(.ended) || event.momentumPhase.contains(.cancelled) { discardedMomentum = false }
                return nil
            }
            let ending = event.phase.contains(.ended) || event.phase.contains(.cancelled)
            if !event.momentumPhase.isEmpty || ending, let route = gestureRoute {
                if route == .canvas { pan(event) }
                else if let target = gestureTarget, target.window === window, target.isDescendant(of: graphRoot) { consumeInner(event, target: target) }
                else { discardedMomentum = true }
                if event.momentumPhase.contains(.ended) || event.momentumPhase.contains(.cancelled) || event.phase.contains(.cancelled) {
                    gestureRoute = nil; gestureTarget = nil; discardedMomentum = false
                }
                return nil
            }
            // A gesture originating outside this viewport remains owned by its
            // original receiver; hovering the graph cannot capture its inertia.
            if !event.momentumPhase.isEmpty { return event }
            if event.phase.isEmpty { gestureRoute = nil; gestureTarget = nil; discardedMomentum = false }
        }
        guard bounds.intersection(visibleRect).contains(point), let content = window.contentView,
              let hit = content.hitTest(content.superview?.convert(event.locationInWindow, from: nil) ?? event.locationInWindow),
              hit === graphRoot || hit === graphRoot.superview || hit.isDescendant(of: graphRoot) else {
            if event.type == .scrollWheel { gestureRoute = nil; gestureTarget = nil }
            return event
        }
        let node = frames.first { $0.1.contains(point) }
        if event.type == .leftMouseDown {
            consumedTargetToken = targetToken
            select(node?.0)
            if node == nil {
                window.makeFirstResponder(self)
                isPanning = true; dragLocation = point; NSCursor.closedHand.push()
                return nil
            }
            // The original click/drag still belongs to native text and tool
            // controls. Only empty canvas starts a graph drag.
            if !(hit is NSTextView), !(hit is NSControl) { window.makeFirstResponder(self) }
            return event
        }
        if event.type == .scrollWheel {
            consumedTargetToken = targetToken
            if let node, node.0 == selectedNodeID {
                let target = innerScroll(nodeID: node.0, frame: node.1, point: point, content: graphRoot)
                if !event.phase.isEmpty { gestureRoute = .inner; gestureTarget = target }
                consumeInner(event, target: target)
            } else {
                if !event.phase.isEmpty { gestureRoute = .canvas; gestureTarget = nil }
                pan(event)
            }
            return nil
        }
        return event
    }
    private func pan(_ event: NSEvent) {
        forwardedWheels += 1
        let scale: CGFloat = event.hasPreciseScrollingDeltas ? 1 : 12
        setPanOffset(CGPoint(x: panOffset.x + event.scrollingDeltaX * scale, y: panOffset.y + event.scrollingDeltaY * scale))
    }
    private func consumeInner(_ event: NSEvent, target: NSScrollView?) {
        nativeWheels += 1
        target?.scrollWheel(with: event)
        // Always consume, including boundaries or blocks without a transcript.
        // The fixed viewport has no outer NSScrollView to receive bubbling.
    }
    private func innerScroll(nodeID: String, frame: CGRect, point: CGPoint, content: NSView) -> NSScrollView? {
        let transcriptID = "transcript-graph-\(sessionID)-\(nodeID)"
        var preferred: NSScrollView?
        var underPointer: NSScrollView?
        func visit(_ view: NSView, depth: Int) {
            guard depth < 64, !view.isHiddenOrHasHiddenAncestor else { return }
            if let scroll = view as? NSScrollView {
                let rect = convert(scroll.bounds, from: scroll)
                if frame.contains(CGPoint(x: rect.midX, y: rect.midY)) {
                    if scroll.documentView?.accessibilityIdentifier() == transcriptID { preferred = scroll }
                    if rect.contains(point) { underPointer = scroll }
                }
            }
            for child in view.subviews { visit(child, depth: depth + 1) }
        }
        visit(content, depth: 0)
        return underPointer ?? preferred
    }
}
