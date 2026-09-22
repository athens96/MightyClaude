import AppKit
import Combine
import SwiftUI

extension NSView {
    /// Hosting overlays can report their ancestor's visibleRect outside their
    /// own bounds. Only the visible part of this exact view is a dock target.
    var paneDockVisibleRect: NSRect {
        isHiddenOrHasHiddenAncestor ? .zero : bounds.intersection(visibleRect)
    }
}

struct PaneDockDragTarget: Equatable {
    let workspaceId: String
    let groupId: String
    let zone: PaneDockZone
    var tabId: String?
    var beforeSessionId: String?
    var afterTab = false
}

/// Docking stays inside one app window. Track the tab's real mouse gesture,
/// independently of nested NSTextView, Ghostty and file-drop destinations.
/// Geometry-only anchors never consume text selection or composer input.
@MainActor
final class PaneDockDragCoordinator: ObservableObject {
    static let shared = PaneDockDragCoordinator()
    @Published private(set) var target: PaneDockDragTarget? {
        didSet {
            guard target != oldValue else { return }
            // Draw the drag preview before the next mouse event, independently
            // of when SwiftUI commits its next view transaction.
            for view in groups.allObjects { view.needsDisplay = true; view.displayIfNeeded() }
            for view in tabs.allObjects { view.needsDisplay = true; view.displayIfNeeded() }
        }
    }
    private weak var store: AppStore?
    private weak var sourceWindow: NSWindow?
    private var payload: PaneDragPayload?
    private var gestureOwner: UUID?
    private var revision: UInt64 = 0
    private let groups = NSHashTable<PaneDockGroupAnchorView>.weakObjects()
    private let tabs = NSHashTable<PaneDockTabHandleView>.weakObjects()
    private(set) var nativeDragStarts = 0
    private(set) var nativeDragCompletions = 0

    func register(_ view: PaneDockGroupAnchorView) { groups.add(view) }
    func register(_ view: PaneDockTabHandleView) { tabs.add(view) }
    func tab(sessionId: String, in window: NSWindow) -> PaneDockTabHandleView? {
        tabs.allObjects.first { $0.sessionId == sessionId && $0.window === window && !$0.paneDockVisibleRect.isEmpty }
    }
    func group(id: String, in window: NSWindow) -> PaneDockGroupAnchorView? {
        groups.allObjects.first { $0.groupId == id && $0.window === window && !$0.paneDockVisibleRect.isEmpty }
    }
    func begin(store: AppStore, sessionId: String, window: NSWindow, owner: UUID? = nil) -> Bool {
        revision &+= 1
        let expected = revision
        clearDrag()
        guard revision == expected else { return false }
        guard let payload = store.beginPaneDrag(sessionId) else { return false }
        guard revision == expected else { store.cancelPaneDrag(payload); return false }
        self.store = store; self.payload = payload; sourceWindow = window
        gestureOwner = owner
        nativeDragStarts += 1
        return true
    }
    func update(location: NSPoint, window: NSWindow, owner: UUID? = nil) {
        guard gestureOwner == owner else { return }
        guard sourceWindow === window, let store, let payload, store.canDropPane(in: payload.workspaceId) else { target = nil; return }
        guard let group = groups.allObjects.first(where: { view in
            view.window === window && view.workspaceId == payload.workspaceId && view.paneDockVisibleRect.contains(view.convert(location, from: nil))
        }) else { target = nil; return }
        if let tab = tabs.allObjects.first(where: { view in
            view.window === window && view.workspaceId == payload.workspaceId && view.groupId == group.groupId && view.paneDockVisibleRect.contains(view.convert(location, from: nil))
        }) {
            let after = tab.convert(location, from: nil).x >= tab.bounds.midX
            target = PaneDockDragTarget(workspaceId: payload.workspaceId, groupId: group.groupId, zone: .center, tabId: tab.sessionId, beforeSessionId: after ? tab.nextSessionId : tab.sessionId, afterTab: after)
        } else {
            let point = group.convert(location, from: nil)
            let zone: PaneDockZone = point.y < 36 ? .center : .resolve(location: point, in: group.bounds.size)
            target = PaneDockDragTarget(workspaceId: payload.workspaceId, groupId: group.groupId, zone: zone)
        }
    }
    func finish(location: NSPoint, window: NSWindow, owner: UUID? = nil) {
        guard gestureOwner == owner else { return }
        let expected = revision
        update(location: location, window: window, owner: owner)
        guard revision == expected, gestureOwner == owner else { return }
        if let store, let payload, let target,
           store.finishPaneDrag(payload, workspaceId: target.workspaceId, groupId: target.groupId, placement: target.zone.placement, beforeSessionId: target.beforeSessionId) {
            nativeDragCompletions += 1
        }
        if revision == expected { cancel() }
    }
    func cancel() {
        revision &+= 1
        clearDrag()
    }
    func cancel(owner: UUID) {
        guard gestureOwner == owner else { return }
        cancel()
    }
    private func clearDrag() {
        let previousStore = store
        let previousPayload = payload
        payload = nil; store = nil; sourceWindow = nil; gestureOwner = nil; target = nil
        // Drop ownership before publishing store changes. A new gesture started
        // by that publication must not be cleared by the retiring gesture.
        if let previousStore, let previousPayload { previousStore.cancelPaneDrag(previousPayload) }
    }
}

struct PaneDockGroupAnchor: NSViewRepresentable {
    let workspaceId: String
    let groupId: String
    func makeNSView(context: Context) -> PaneDockGroupAnchorView { PaneDockGroupAnchorView() }
    func updateNSView(_ view: PaneDockGroupAnchorView, context: Context) {
        view.workspaceId = workspaceId; view.groupId = groupId
        PaneDockDragCoordinator.shared.register(view)
    }
}

final class PaneDockGroupAnchorView: NSView {
    var workspaceId = ""
    var groupId = ""
    private(set) var previewDrawCount = 0
    override var isFlipped: Bool { true }
    override func hitTest(_ point: NSPoint) -> NSView? { nil }
    override func isAccessibilityElement() -> Bool { false }
    override func draw(_ dirtyRect: NSRect) {
        guard let target = PaneDockDragCoordinator.shared.target, target.groupId == groupId,
              target.workspaceId == workspaceId, target.tabId == nil else { return }
        previewDrawCount += 1
        let rect = target.zone.previewRect(in: bounds.size)
        let color = NSColor(Palette.accent)
        let path = NSBezierPath(roundedRect: rect, xRadius: 9, yRadius: 9)
        color.withAlphaComponent(0.16).setFill(); path.fill()
        color.setStroke(); path.lineWidth = 2; path.setLineDash([6, 4], count: 2, phase: 0); path.stroke()
        let attributes: [NSAttributedString.Key: Any] = [.font: NSFont.systemFont(ofSize: 12, weight: .semibold), .foregroundColor: color]
        let text = target.zone.title as NSString; let size = text.size(withAttributes: attributes)
        let textRect = NSRect(x: rect.midX - size.width / 2, y: rect.midY - size.height / 2, width: size.width, height: size.height)
        NSColor.windowBackgroundColor.withAlphaComponent(0.95).setFill()
        NSBezierPath(roundedRect: textRect.insetBy(dx: -12, dy: -7), xRadius: 15, yRadius: 15).fill()
        text.draw(in: textRect, withAttributes: attributes)
    }
}

struct PaneDockTabHandle: NSViewRepresentable {
    let store: AppStore
    let sessionId: String
    let workspaceId: String
    let groupId: String
    let title: String
    let nextSessionId: String?
    func makeNSView(context: Context) -> PaneDockTabHandleView { PaneDockTabHandleView() }
    func updateNSView(_ view: PaneDockTabHandleView, context: Context) {
        view.store = store; view.sessionId = sessionId; view.workspaceId = workspaceId; view.groupId = groupId; view.nextSessionId = nextSessionId
        view.setAccessibilityElement(true); view.setAccessibilityRole(.button); view.setAccessibilityLabel(title)
        view.setAccessibilityHelp("선택하거나 끌어서 탭을 합치고 가장자리에서 분할합니다.")
        view.setAccessibilityIdentifier("pane-tab-drag-" + sessionId)
        PaneDockDragCoordinator.shared.register(view)
    }
    static func dismantleNSView(_ view: PaneDockTabHandleView, coordinator: ()) { view.cancelGesture() }
}

final class PaneDockTabHandleView: NSView {
    weak var store: AppStore? { didSet { if store !== oldValue { cancelGesture() } } }
    var sessionId = "" { didSet { if sessionId != oldValue { cancelGesture() } } }
    var workspaceId = "" { didSet { if workspaceId != oldValue { cancelGesture() } } }
    var groupId = "" { didSet { if groupId != oldValue { cancelGesture() } } }
    var nextSessionId: String?
    private final class Gesture {
        let id = UUID()
        weak var window: NSWindow?
        weak var store: AppStore?
        let start: NSPoint
        let clickCount: Int
        var dragging = false
        var pushedCursor = false
        init(window: NSWindow, store: AppStore, event: NSEvent) {
            self.window = window; self.store = store
            start = event.locationInWindow; clickCount = event.clickCount
        }
    }
    private static weak var activeHandle: PaneDockTabHandleView?
    private static var mouseDownRevision: UInt64 = 0
    private var gesture: Gesture?
    private var gestureMonitor: Any?
    private var gestureObservers: [(NotificationCenter, NSObjectProtocol)] = []
    private var releaseWatch: Timer?
    struct GestureEnvironment {
        var isLeftMouseButtonDown: () -> Bool
        var pushCursor: () -> Void
        var popCursor: () -> Void
        static var live: Self {
            Self(isLeftMouseButtonDown: { NSEvent.pressedMouseButtons & 1 != 0 },
                 pushCursor: { NSCursor.closedHand.push() }, popCursor: { NSCursor.pop() })
        }
    }
    /// Read hardware button state without dequeuing input. Isolated fixtures
    /// replace these effects without changing the user's cursor or mouse state.
    var gestureEnvironment = GestureEnvironment.live

    deinit {
        if let gestureMonitor { NSEvent.removeMonitor(gestureMonitor) }
        gestureObservers.forEach { $0.0.removeObserver($0.1) }
        releaseWatch?.invalidate()
    }
    override var isFlipped: Bool { true }
    override var mouseDownCanMoveWindow: Bool { false }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override func viewWillMove(toWindow newWindow: NSWindow?) {
        if newWindow !== window { cancelGesture() }
        super.viewWillMove(toWindow: newWindow)
    }
    override func viewDidHide() { cancelGesture(); super.viewDidHide() }
    override func draw(_ dirtyRect: NSRect) {
        guard let target = PaneDockDragCoordinator.shared.target, target.tabId == sessionId, target.groupId == groupId else { return }
        NSColor(Palette.accent).setFill()
        NSBezierPath(roundedRect: NSRect(x: target.afterTab ? bounds.maxX - 2 : bounds.minX, y: 2, width: 2, height: max(0, bounds.height - 4)), xRadius: 1, yRadius: 1).fill()
    }
    override func accessibilityPerformPress() -> Bool {
        guard let store, !store.hasModal else { return false }; store.selectSession(sessionId); return true
    }
    override func mouseDown(with event: NSEvent) {
        Self.mouseDownRevision &+= 1
        let expected = Self.mouseDownRevision
        Self.activeHandle?.cancelGesture()
        guard Self.mouseDownRevision == expected,
              event.type == .leftMouseDown, !event.modifierFlags.contains(.control),
              let window, event.window === window, let store, !store.hasModal,
              window.isVisible, !window.isMiniaturized, window.attachedSheet == nil,
              NSApp.modalWindow == nil, !paneDockVisibleRect.isEmpty else { return }
        // Select on press, like system tab bars: a click that wobbles a few
        // points still switches tabs instead of turning into a cancelled drag.
        let identity = (sessionId, workspaceId, groupId)
        store.selectSession(sessionId)
        guard Self.mouseDownRevision == expected, self.window === window, self.store === store,
              identity == (sessionId, workspaceId, groupId), !paneDockVisibleRect.isEmpty else { return }
        gesture = Gesture(window: window, store: store, event: event)
        Self.activeHandle = self
        installGestureObservers(window: window)
        // Return to AppKit immediately. A nested nextEvent loop can swallow all
        // typing and activation events when a mouse-up is lost during a switch.
    }
    override func mouseDragged(with event: NSEvent) {
        guard let current = validatedGesture(), let window = current.window,
              event.window === window, let store = current.store else { cancelGesture(); return }
        let coordinator = PaneDockDragCoordinator.shared
        let point = event.locationInWindow
        if !current.dragging, hypot(point.x - current.start.x, point.y - current.start.y) >= 5 {
            guard coordinator.begin(store: store, sessionId: sessionId, window: window, owner: current.id) else {
                if gesture === current { cancelGesture() }
                return
            }
            current.dragging = true
            guard gesture === current else { coordinator.cancel(owner: current.id); return }
            gestureEnvironment.pushCursor(); current.pushedCursor = true
        }
        if current.dragging { coordinator.update(location: point, window: window, owner: current.id) }
    }
    override func mouseUp(with event: NSEvent) {
        guard let current = validatedGesture(), let window = current.window,
              event.window === window, let store = current.store else { cancelGesture(); return }
        let rename = !current.dragging && current.clickCount == 2
            && paneDockVisibleRect.contains(convert(event.locationInWindow, from: nil))
        // Finishing a drop may remove/rebind this view. Retire its gesture first
        // so a teardown callback cannot cancel a newly selected pane's gesture.
        finishGesture(cancelDrag: false)
        if current.dragging { PaneDockDragCoordinator.shared.finish(location: event.locationInWindow, window: window, owner: current.id) }
        else if rename { store.beginRenameSession(sessionId) }
    }

    private func validatedGesture() -> Gesture? {
        guard let current = gesture else { return nil }
        guard Self.activeHandle === self, let window = current.window, self.window === window,
              let store = current.store, self.store === store, !store.hasModal,
              NSApp.isActive, NSApp.keyWindow === window, window.isKeyWindow, window.isVisible, !window.isMiniaturized,
              window.isOnActiveSpace, window.attachedSheet == nil, NSApp.modalWindow == nil,
              !paneDockVisibleRect.isEmpty else { cancelGesture(); return nil }
        return current
    }

    private func installGestureObservers(window: NSWindow) {
        gestureMonitor = NSEvent.addLocalMonitorForEvents(matching: [.keyDown, .leftMouseDown, .rightMouseDown, .otherMouseDown]) { [weak self] event in
            guard let self else { return event }
            return self.handleGestureEvent(event)
        }
        for (center, name, object) in [
            (NotificationCenter.default, NSApplication.didResignActiveNotification, NSApp as AnyObject?),
            (NotificationCenter.default, NSWindow.didResignKeyNotification, window as AnyObject?),
            (NotificationCenter.default, NSWindow.willCloseNotification, window as AnyObject?),
            (NotificationCenter.default, NSWindow.didMiniaturizeNotification, window as AnyObject?),
            (NSWorkspace.shared.notificationCenter, NSWorkspace.activeSpaceDidChangeNotification, nil),
        ] {
            let observer = center.addObserver(forName: name, object: object, queue: .main) { [weak self] _ in
                MainActor.assumeIsolated { self?.cancelGesture() }
            }
            gestureObservers.append((center, observer))
        }
        let watch = Timer(timeInterval: 0.1, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.checkForLostMouseUp() }
        }
        releaseWatch = watch
        RunLoop.main.add(watch, forMode: .common)
    }

    private func handleGestureEvent(_ event: NSEvent) -> NSEvent? {
        guard gesture != nil else { return event }
        if event.type == .leftMouseDown || event.type == .rightMouseDown || event.type == .otherMouseDown {
            cancelGesture()
            return event
        }
        // Every printable key and shortcut keeps normal AppKit/IME dispatch.
        guard event.type == .keyDown, event.keyCode == 53,
              let current = validatedGesture(), event.window === current.window else { return event }
        cancelGesture()
        return nil
    }

    private func checkForLostMouseUp() {
        guard validatedGesture() != nil else { return }
        if !gestureEnvironment.isLeftMouseButtonDown() { cancelGesture() }
    }

    func cancelGesture() { finishGesture(cancelDrag: true) }
    private func finishGesture(cancelDrag: Bool) {
        guard let current = gesture else { return }
        gesture = nil
        if Self.activeHandle === self { Self.activeHandle = nil }
        if let gestureMonitor { NSEvent.removeMonitor(gestureMonitor) }
        gestureMonitor = nil
        gestureObservers.forEach { $0.0.removeObserver($0.1) }; gestureObservers.removeAll()
        releaseWatch?.invalidate(); releaseWatch = nil
        if current.pushedCursor { gestureEnvironment.popCursor(); current.pushedCursor = false }
        if cancelDrag, current.dragging { PaneDockDragCoordinator.shared.cancel(owner: current.id) }
    }
    override func menu(for event: NSEvent) -> NSMenu? {
        guard let store, !store.hasModal else { return nil }
        let menu = NSMenu()
        for (title, action) in [("이름 변경…", #selector(renamePane)), ("집중 보기", #selector(focusPane)), ("탭 닫기", #selector(closePane))] {
            let item = NSMenuItem(title: title, action: action, keyEquivalent: ""); item.target = self; menu.addItem(item)
        }
        return menu
    }
    @objc private func renamePane() { store?.beginRenameSession(sessionId) }
    @objc private func focusPane() { store?.setPaneFocus(true, sessionId: sessionId) }
    @objc private func closePane() { store?.closeSession(sessionId) }
}
