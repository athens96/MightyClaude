import AppKit
import MightyCore
import SwiftUI

/// Placement of the docked reference bubble. The native host and the probe
/// derive its frame from their own viewport size, so no measurement crosses
/// SwiftUI hosting boundaries.
struct MightyOverlayLayout: Equatable {
    var onLeft: Bool
    var storedWidth: Double
    var storedHeight: Double
    func frame(in canvas: CGSize) -> CGRect {
        MightyGraphReferenceBubble.frame(onLeft: onLeft, canvas: canvas, storedWidth: storedWidth, storedHeight: storedHeight)
    }
}

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
    var onResize: (String, CGSize, Bool) -> Void = { _, _, _ in }
    /// A layout pass left no card on screen without the camera having moved.
    var onStranded: (MightyGraphCamera.StrandedWatch.Loss) -> Void = { _ in }
    /// A new newest request aims the camera by its own rule, not the stranded net's.
    var newestRunID: String?
    /// Chrome docked over the diagram, such as the reference bubble. It lives
    /// in its own native view so AppKit, not SwiftUI layering, decides hits,
    /// and the probe resizes it exactly the way it resizes graph blocks.
    var overlay: AnyView = AnyView(EmptyView())
    var overlayLayout: MightyOverlayLayout? = nil
    var onOverlayResize: (CGSize, Bool) -> Void = { _, _ in }
    let content: Content

    func makeNSView(context: Context) -> MightyGraphViewportHost<Content> {
        MightyGraphViewportHost(content: content)
    }
    func updateNSView(_ host: MightyGraphViewportHost<Content>, context: Context) {
        host.hosting.rootView = content
        host.overlayHost.rootView = overlay
        host.overlayLayout = overlayLayout
        host.setAccessibilityIdentifier("mighty-graph-canvas-" + sessionID)
        let view = host.probe
        view.setAccessibilityIdentifier("mighty-graph-interaction-" + sessionID)
        view.sessionID = sessionID
        view.expectedViewportSize = viewportSize
        view.selectedNodeID = selection
        view.auxiliaryNodeIDs = Set(nodes.compactMap { node in
            if case .resultFiles = node.content { return node.id }
            return nil
        })
        view.targetToken = targetToken
        view.targetFrame = targetFrame
        view.alignTop = alignTop
        view.onSelect = { selection = $0 }
        view.onPan = { panOffset = $0 }
        view.onResize = onResize
        view.onStranded = onStranded
        view.newestRunID = newestRunID
        view.overlayLayout = overlayLayout
        view.onOverlayResize = onOverlayResize
        view.updateLayoutFrames(nodes.map { ($0.id, $0.frame) }, zoom: zoom, panOffset: panOffset)
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
    let overlayHost = MightyGraphOverlayHost(rootView: AnyView(EmptyView()))
    /// nil hides the docked chrome. Its native view is sized to exactly the
    /// bubble frame for this viewport, so AppKit hit-testing alone decides
    /// what belongs to the bubble and what belongs to the diagram.
    var overlayLayout: MightyOverlayLayout? { didSet { if overlayLayout != oldValue { needsLayout = true } } }
    override var isFlipped: Bool { true }
    init(content: Content) {
        hosting = NSHostingView(rootView: content)
        super.init(frame: .zero)
        hosting.sizingOptions = []; overlayHost.sizingOptions = []
        setAccessibilityElement(true)
        setAccessibilityRole(.group)
        setAccessibilityLabel("마이티 그래프")
        wantsLayer = true; layer?.masksToBounds = true
        addSubview(hosting); addSubview(probe); addSubview(overlayHost)
        probe.graphRoot = hosting; probe.setAccessibilityElement(false)
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
    override func accessibilityChildren() -> [Any]? { (hosting.accessibilityChildren() ?? []) + (overlayHost.accessibilityChildren() ?? []) }
    override func layout() {
        super.layout(); hosting.frame = bounds; probe.frame = bounds
        overlayHost.frame = overlayLayout?.frame(in: bounds.size) ?? .zero; overlayHost.isHidden = overlayLayout == nil
        probe.scheduleInitialPosition()
    }
}

/// Hosts the docked chrome. It is sized to the chrome itself, so outside it
/// there is simply no view to hit and the diagram beneath receives the event.
@MainActor
final class MightyGraphOverlayHost: NSHostingView<AnyView> {}

@MainActor
final class MightyGraphInteractionProbe: NSView {
    weak var graphRoot: NSView?
    var sessionID = ""
    var layoutFrames: [(String, CGRect)] = []
    var zoom: CGFloat = 1
    var expectedViewportSize: CGSize = .zero
    var panOffset: CGPoint = .zero
    var selectedNodeID: String?
    /// Attached file lists scroll immediately and do not expose block resizing.
    var auxiliaryNodeIDs: Set<String> = []
    var onSelect: (String?) -> Void = { _ in }
    var onPan: (CGPoint) -> Void = { _ in }
    var onResize: (String, CGSize, Bool) -> Void = { _, _, _ in }
    var onStranded: (MightyGraphCamera.StrandedWatch.Loss) -> Void = { _ in }
    /// A new newest request aims the camera by its own rule, not the stranded net's.
    var newestRunID: String?
    var targetToken: String?
    var targetFrame: CGRect?
    var alignTop = false
    /// Docked chrome placement; events inside its frame are never a pan or a
    /// block selection, and its corner handle is resized here like a block.
    var overlayLayout: MightyOverlayLayout?
    var onOverlayResize: (CGSize, Bool) -> Void = { _, _ in }
    private struct OverlayResizeDrag {
        let initialPoint: CGPoint
        let initialSize: CGSize
        let onLeft: Bool
        var size: CGSize
        var changed = false
    }
    private var overlayResize: OverlayResizeDrag?
    var overlayFrame: CGRect? { overlayLayout?.frame(in: bounds.size) }
    static let overlayHandleSide: CGFloat = 26
    /// The bubble's bottom corner that faces the diagram, inside the panel.
    var overlayHandleRect: CGRect? {
        guard let layout = overlayLayout, let frame = overlayFrame else { return nil }
        let side = Self.overlayHandleSide
        let x = layout.onLeft ? frame.maxX - MightyBubbleShape.tailLength - side : frame.minX + MightyBubbleShape.tailLength
        return CGRect(x: x, y: frame.maxY - side, width: side, height: side)
    }
    private(set) var consumedTargetToken: String?
    private(set) var forwardedWheels = 0
    private(set) var nativeWheels = 0
    private(set) var isPanning = false
    var isResizing: Bool { resizeDrag != nil }
    private struct ResizeDrag {
        let id: String
        let initialFrame: CGRect
        let initialPoint: CGPoint
        let initialZoom: CGFloat
        let anchor: CGPoint
        var size: CGSize
        var changed = false
    }
    private struct ResizeAnchor { let id: String; let point: CGPoint; let size: CGSize }
    private var resizeDrag: ResizeDrag?
    private var finishingAnchor: ResizeAnchor?
    private var pendingPanNotification = false
    private var pendingStrandedNotification = false
    private var strandedWatch = MightyGraphCamera.StrandedWatch()
    private var strandedRetryScheduled = false
    private static let strandedRetries = 6
    private var strandedRetriesLeft = 0
    /// Wheel momentum and the tail of a drag still belong to the user.
    private static let userMoveGrace: TimeInterval = 0.5
    private var lastUserMoveAt: TimeInterval = 0
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
         "selectedNodeID": selectedNodeID ?? "nil", "isPanning": String(isPanning), "isResizing": String(isResizing)]
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
        finishResize(cancelled: true)
        finishOverlayResize(cancelled: true)
        if isPanning { NSCursor.pop() }
        isPanning = false; dragLocation = nil
        gestureRoute = nil; gestureTarget = nil; discardedMomentum = false
    }
    func dispose() {
        disposed = true; removeMonitoring(); onSelect = { _ in }; onPan = { _ in }; onResize = { _, _, _ in }; onStranded = { _ in }; layoutFrames = []; finishingAnchor = nil
    }
    /// Layout may recenter a parent when its child's width changes. Keep the
    /// dragged card's original top-left viewport point pinned across that reflow.
    func updateLayoutFrames(_ frames: [(String, CGRect)], zoom newZoom: CGFloat, panOffset incomingPan: CGPoint) {
        strandedRetriesLeft = Self.strandedRetries
        notifyIfStranded(frames, incomingZoom: newZoom, incomingPan: incomingPan)
        layoutFrames = frames; zoom = newZoom; panOffset = incomingPan
        let anchor = resizeDrag.map { ResizeAnchor(id: $0.id, point: $0.anchor, size: $0.size) } ?? finishingAnchor
        guard let anchor else { return }
        guard let frame = frames.first(where: { $0.0 == anchor.id })?.1 else {
            cancelInteraction(); finishingAnchor = nil; return
        }
        let adjusted = CGPoint(x: anchor.point.x - frame.minX * zoom, y: anchor.point.y - frame.minY * zoom)
        if adjusted != panOffset {
            panOffset = adjusted
            // Publishing a SwiftUI binding during updateNSView is undefined.
            // Coalesce the correction after the current layout update instead.
            if !pendingPanNotification {
                pendingPanNotification = true
                DispatchQueue.main.async { [weak self] in
                    guard let self else { return }
                    self.pendingPanNotification = false
                    guard !self.disposed else { return }
                    self.onPan(self.panOffset)
                }
            }
        }
        if resizeDrag == nil, adjusted == incomingPan,
           abs(frame.width - anchor.size.width) < 0.5, abs(frame.height - anchor.size.height) < 0.5 { finishingAnchor = nil }
    }
    /// Only a settled pass is judged: a drag, a resize, a move the user just
    /// made and a camera target still waiting for admission all decide where
    /// the camera goes themselves. An unsettled pass is judged later.
    private func notifyIfStranded(_ frames: [(String, CGRect)], incomingZoom: CGFloat, incomingPan: CGPoint) {
        guard !disposed else { return }
        let idle = ProcessInfo.processInfo.systemUptime - lastUserMoveAt > Self.userMoveGrace
        let targetSettled = targetToken == nil || targetToken == consumedTargetToken
        let settled = !pendingStrandedNotification && !isResizing && finishingAnchor == nil && !isPanning && idle
            && targetSettled && incomingPan == panOffset && admissibleViewport && window != nil && !isHiddenOrHasHiddenAncestor
        guard let loss = strandedWatch.observe(nodes: frames, camera: incomingPan, zoom: incomingZoom, viewport: bounds.size,
                                               newestRunID: newestRunID, settled: settled) else {
            retryWithheldVerdict(); return
        }
        // Publishing SwiftUI state during updateNSView is undefined, and one
        // layout pass may only ask for one re-aim.
        pendingStrandedNotification = true
        let detectedToken = targetToken
        DispatchQueue.main.async { [weak self] in
            guard let self else { return }
            self.pendingStrandedNotification = false
            // A target published in the same turn — a new request — wins.
            guard !self.disposed, !self.isPanning, self.targetToken == detectedToken else { return }
            self.onStranded(loss)
        }
    }
    /// The withheld pass may have been the last one of a burst — a run's final
    /// event — and then no later pass would ever come to judge it.
    private func retryWithheldVerdict() {
        // Bounded: a hidden pane or a target that never lands stays unsettled.
        guard strandedWatch.isWithholding, !strandedRetryScheduled, strandedRetriesLeft > 0 else { return }
        strandedRetryScheduled = true; strandedRetriesLeft -= 1
        DispatchQueue.main.asyncAfter(deadline: .now() + Self.userMoveGrace + 0.05) { [weak self] in
            guard let self else { return }
            self.strandedRetryScheduled = false
            guard !self.disposed else { return }
            self.notifyIfStranded(self.layoutFrames, incomingZoom: self.zoom, incomingPan: self.panOffset)
        }
    }
    func resizeHandleRect(for frame: CGRect) -> CGRect {
        let side = max(16, 22 * zoom)
        return CGRect(x: frame.maxX - side, y: frame.maxY - side, width: side, height: side)
    }
    private func beginResize(id: String, point: CGPoint, frame: CGRect) {
        guard zoom.isFinite, zoom > 0, let graphFrame = layoutFrames.first(where: { $0.0 == id })?.1 else { return }
        finishingAnchor = nil
        gestureRoute = nil; gestureTarget = nil; discardedMomentum = false
        resizeDrag = ResizeDrag(id: id, initialFrame: graphFrame, initialPoint: point, initialZoom: zoom,
                                anchor: frame.origin, size: graphFrame.size)
        window?.makeFirstResponder(self)
        NSCursor.crosshair.push()
    }
    private func resize(to point: CGPoint) {
        guard var drag = resizeDrag else { return }
        let width = min(1400, max(300, drag.initialFrame.width + (point.x - drag.initialPoint.x) / drag.initialZoom))
        let height = min(1200, max(140, drag.initialFrame.height + (point.y - drag.initialPoint.y) / drag.initialZoom))
        let size = CGSize(width: width, height: height)
        guard size != drag.size else { return }
        drag.size = size; drag.changed = true; resizeDrag = drag
        onResize(drag.id, size, false)
    }
    private func finishResize(cancelled: Bool) {
        guard let drag = resizeDrag else { return }
        resizeDrag = nil; NSCursor.pop()
        // A camera target published during the drag was held back; nothing else re-admits it when the size never changed.
        scheduleInitialPosition()
        guard drag.changed else { return }
        let size = cancelled ? drag.initialFrame.size : drag.size
        finishingAnchor = ResizeAnchor(id: drag.id, point: drag.anchor, size: size)
        onResize(drag.id, size, true)
    }
    private func beginOverlayResize(at point: CGPoint, frame: CGRect, onLeft: Bool) {
        finishingAnchor = nil
        gestureRoute = nil; gestureTarget = nil; discardedMomentum = false
        let panel = CGSize(width: frame.width - MightyBubbleShape.tailLength, height: frame.height)
        overlayResize = OverlayResizeDrag(initialPoint: point, initialSize: panel, onLeft: onLeft, size: panel)
        window?.makeFirstResponder(self)
        NSCursor.crosshair.push()
    }
    private func overlayResize(to point: CGPoint) {
        guard var drag = overlayResize else { return }
        let dx = point.x - drag.initialPoint.x, dy = point.y - drag.initialPoint.y
        let margin = MightyGraphReferenceBubble.margin
        let maxWidth = max(MightyGraphReferenceBubble.minimumWidth, (bounds.width - margin * 2) * 0.85)
        let maxHeight = max(MightyGraphReferenceBubble.minimumHeight, bounds.height - margin * 2)
        let size = CGSize(width: min(maxWidth, max(MightyGraphReferenceBubble.minimumWidth, drag.initialSize.width + (drag.onLeft ? dx : -dx))),
                          height: min(maxHeight, max(MightyGraphReferenceBubble.minimumHeight, drag.initialSize.height + dy)))
        guard size != drag.size else { return }
        drag.size = size; drag.changed = true; overlayResize = drag
        onOverlayResize(size, false)
    }
    private func finishOverlayResize(cancelled: Bool) {
        guard let drag = overlayResize else { return }
        overlayResize = nil; NSCursor.pop()
        scheduleInitialPosition()
        guard drag.changed else { return }
        onOverlayResize(cancelled ? drag.initialSize : drag.size, true)
    }

    func setPanOffset(_ value: CGPoint) {
        guard value.x.isFinite, value.y.isFinite else { return }
        if !isResizing { finishingAnchor = nil }
        commitInteractionPosition(value)
    }
    /// An input event may arrive before the deferred initial camera admission.
    /// Commit even an unchanged position once, so the SwiftUI fallback stops
    /// following target geometry after a click or an inner-content scroll.
    private func commitInteractionPosition(_ value: CGPoint) {
        // A degenerate or stale viewport produces offsets that put the document
        // off screen, and committing one also consumes the pending admission
        // that would have repaired it. Refusing must not lose the event either:
        // the admission is rescheduled so the camera still arrives.
        guard admissibleViewport else { scheduleInitialPosition(); return }
        let pendingInitialPosition = targetToken != nil && targetToken != consumedTargetToken
        let targetCamera = targetFrame.map { MightyGraphLayout.cameraOffset(for: $0, viewport: bounds.size, zoom: zoom, alignTop: alignTop) }
        guard let committed = MightyGraphCamera.admittedCamera(targetToken: targetToken, consumedToken: consumedTargetToken,
                                                               targetCamera: targetCamera, current: panOffset, requested: value) else {
            // A published target without a frame yet: leave its token pending
            // rather than commit an offset the re-aim was about to replace.
            scheduleInitialPosition(); return
        }
        consumedTargetToken = targetToken
        lastUserMoveAt = ProcessInfo.processInfo.systemUptime
        if pendingInitialPosition && committed == panOffset { onPan(committed) }
        else { applyPan(committed) }
    }
    /// The camera is admitted for the viewport SwiftUI measured. A probe that
    /// was never told one — a hand-built fixture — speaks for its own bounds
    /// instead; only a genuinely degenerate viewport is refused.
    private var admissibleViewport: Bool {
        guard bounds.width > 0, bounds.height > 0 else { return false }
        guard expectedViewportSize != .zero else { return true }
        return abs(bounds.width - expectedViewportSize.width) < 1 && abs(bounds.height - expectedViewportSize.height) < 1
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
            guard !self.disposed, !self.isResizing, self.window != nil, let token = self.targetToken, token != self.consumedTargetToken,
                  let frame = self.targetFrame, self.admissibleViewport else { return }
            self.consumedTargetToken = token
            let initial = MightyGraphLayout.cameraOffset(for: frame, viewport: self.bounds.size, zoom: self.zoom, alignTop: self.alignTop)
            if initial == self.panOffset {
                // The first SwiftUI render already used this offset. Commit it
                // as camera state so later layout changes do not keep centering.
                self.onPan(initial)
            } else { self.applyPan(initial) }
        }
    }
    private func select(_ id: String?) {
        guard selectedNodeID != id else { return }
        selectedNodeID = id; onSelect(id)
    }
    private func handle(_ event: NSEvent) -> NSEvent? {
        guard !disposed, let window, window.attachedSheet == nil,
              !isHiddenOrHasHiddenAncestor, let graphRoot else { cancelInteraction(); return event }
        // A local monitor runs before NSWindow dispatch. Consuming the first
        // click in an inactive window also consumes AppKit's chance to make
        // it key. Let AppKit establish activation before owning any gesture;
        // a drag that outlived activation must release its capture as well.
        guard NSApp.isActive, window.isKeyWindow, NSApp.keyWindow === window else {
            cancelInteraction()
            return event
        }
        if isResizing {
            if event.type == .scrollWheel { return nil }
            if event.type == .leftMouseDragged || event.type == .leftMouseUp {
                let windowPoint: CGPoint
                if event.window === window { windowPoint = event.locationInWindow }
                else { windowPoint = window.convertPoint(fromScreen: event.window?.convertPoint(toScreen: event.locationInWindow) ?? event.locationInWindow) }
                resize(to: convert(windowPoint, from: nil))
                if event.type == .leftMouseUp { finishResize(cancelled: false) }
                return nil
            }
            if event.type == .keyDown, event.keyCode == 53 { finishResize(cancelled: true); return nil }
        }
        if overlayResize != nil {
            if event.type == .scrollWheel { return nil }
            if event.type == .leftMouseDragged || event.type == .leftMouseUp {
                let windowPoint: CGPoint
                if event.window === window { windowPoint = event.locationInWindow }
                else { windowPoint = window.convertPoint(fromScreen: event.window?.convertPoint(toScreen: event.locationInWindow) ?? event.locationInWindow) }
                overlayResize(to: convert(windowPoint, from: nil))
                if event.type == .leftMouseUp { finishOverlayResize(cancelled: false) }
                return nil
            }
            if event.type == .keyDown, event.keyCode == 53 { finishOverlayResize(cancelled: true); return nil }
        }
        guard event.window === window else { return event }
        if event.type == .keyDown {
            guard event.keyCode == 53 else { return event }
            if isPanning { cancelInteraction(); return nil }
            guard selectedNodeID != nil, let responder = window.firstResponder as? NSView else { return event }
            if let text = responder as? NSTextView, text.hasMarkedText() { return event }
            guard responder === self || responder === graphRoot || responder.isDescendant(of: graphRoot) else { return event }
            select(nil); return nil
        }
        let point = convert(event.locationInWindow, from: nil)
        if !isPanning, !isResizing, let layout = overlayLayout, let frame = overlayFrame, frame.contains(point),
           bounds.intersection(visibleRect).contains(point), !isHiddenOrHasHiddenAncestor {
            if event.type == .leftMouseDown, let handle = overlayHandleRect, handle.contains(point) {
                commitInteractionPosition(panOffset)
                if event.clickCount >= 2 { onOverlayResize(.zero, true); return nil }
                beginOverlayResize(at: point, frame: frame, onLeft: layout.onLeft)
                return nil
            }
            if event.type == .scrollWheel { gestureRoute = nil; gestureTarget = nil }
            return event
        }
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
            commitInteractionPosition(panOffset)
            select(node?.0)
            if let node, !auxiliaryNodeIDs.contains(node.0), resizeHandleRect(for: node.1).contains(point) {
                beginResize(id: node.0, point: point, frame: node.1)
                return nil
            }
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
            commitInteractionPosition(panOffset)
            if let node, node.0 == selectedNodeID || auxiliaryNodeIDs.contains(node.0) {
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
        var auxiliaryScroll: NSScrollView?
        func visit(_ view: NSView, depth: Int) {
            guard depth < 64, !view.isHiddenOrHasHiddenAncestor else { return }
            if let scroll = view as? NSScrollView {
                let rect = convert(scroll.bounds, from: scroll)
                if frame.contains(CGPoint(x: rect.midX, y: rect.midY)) {
                    if auxiliaryNodeIDs.contains(nodeID) { auxiliaryScroll = scroll }
                    if scroll.documentView?.accessibilityIdentifier() == transcriptID { preferred = scroll }
                    if rect.contains(point) { underPointer = scroll }
                }
            }
            for child in view.subviews { visit(child, depth: depth + 1) }
        }
        visit(content, depth: 0)
        return underPointer ?? preferred ?? auxiliaryScroll
    }
}
