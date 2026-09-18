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
            // SwiftUI may defer view transactions in an AppKit tracking loop.
            // Draw the drag preview natively before the next mouse event.
            for view in groups.allObjects { view.needsDisplay = true; view.displayIfNeeded() }
            for view in tabs.allObjects { view.needsDisplay = true; view.displayIfNeeded() }
        }
    }
    private weak var store: AppStore?
    private weak var sourceWindow: NSWindow?
    private var payload: PaneDragPayload?
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
    func begin(store: AppStore, sessionId: String, window: NSWindow) -> Bool {
        cancel()
        guard let payload = store.beginPaneDrag(sessionId) else { return false }
        self.store = store; self.payload = payload; sourceWindow = window
        nativeDragStarts += 1
        return true
    }
    func update(location: NSPoint, window: NSWindow) {
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
    func finish(location: NSPoint, window: NSWindow) {
        update(location: location, window: window)
        if let store, let payload, let target,
           store.finishPaneDrag(payload, workspaceId: target.workspaceId, groupId: target.groupId, placement: target.zone.placement, beforeSessionId: target.beforeSessionId) {
            nativeDragCompletions += 1
        }
        cancel()
    }
    func cancel() {
        if let store, let payload { store.cancelPaneDrag(payload) }
        payload = nil; store = nil; sourceWindow = nil; target = nil
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
}

final class PaneDockTabHandleView: NSView {
    weak var store: AppStore?
    var sessionId = ""
    var workspaceId = ""
    var groupId = ""
    var nextSessionId: String?
    override var isFlipped: Bool { true }
    override var mouseDownCanMoveWindow: Bool { false }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override func draw(_ dirtyRect: NSRect) {
        guard let target = PaneDockDragCoordinator.shared.target, target.tabId == sessionId, target.groupId == groupId else { return }
        NSColor(Palette.accent).setFill()
        NSBezierPath(roundedRect: NSRect(x: target.afterTab ? bounds.maxX - 2 : bounds.minX, y: 2, width: 2, height: max(0, bounds.height - 4)), xRadius: 1, yRadius: 1).fill()
    }
    override func accessibilityPerformPress() -> Bool {
        guard let store, !store.hasModal else { return false }; store.selectSession(sessionId); return true
    }
    override func mouseDown(with event: NSEvent) {
        guard let window, let store, !store.hasModal else { return }
        // Select on press, like system tab bars: a click that wobbles a few
        // points still switches tabs instead of turning into a cancelled drag.
        store.selectSession(sessionId)
        let start = event.locationInWindow
        let coordinator = PaneDockDragCoordinator.shared
        var dragging = false
        var pushedCursor = false
        defer { coordinator.cancel(); if pushedCursor { NSCursor.pop() } }
        // Apple's standard mouse-tracking loop keeps a drag owned by this tab
        // even while the pointer crosses a native editor or terminal child view.
        while self.window === window && window.isVisible {
            guard let next = window.nextEvent(matching: [.leftMouseDragged, .leftMouseUp, .keyDown], until: Date(timeIntervalSinceNow: 0.1), inMode: .eventTracking, dequeue: true) else {
                if !window.isKeyWindow { return }
                continue
            }
            if next.type == .keyDown { if next.keyCode == 53 { return }; continue }
            guard next.windowNumber == window.windowNumber else { return }
            if next.type == .leftMouseDragged {
                let point = next.locationInWindow
                if !dragging && hypot(point.x - start.x, point.y - start.y) >= 5 {
                    dragging = coordinator.begin(store: store, sessionId: sessionId, window: window)
                    guard dragging else { return }
                    NSCursor.closedHand.push(); pushedCursor = true
                }
                if dragging { coordinator.update(location: point, window: window) }
            } else {
                if dragging { coordinator.finish(location: next.locationInWindow, window: window) }
                else if event.clickCount == 2, paneDockVisibleRect.contains(convert(next.locationInWindow, from: nil)) {
                    store.beginRenameSession(sessionId)
                }
                return
            }
        }
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
