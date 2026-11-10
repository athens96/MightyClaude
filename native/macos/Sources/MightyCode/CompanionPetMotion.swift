import AppKit
import Combine
import SwiftUI

/// Codex v1 row order, verified against the bundled pet manifest/contact sheet.
enum CompanionPetAnimation: Int {
    case idle = 0, walkRight, walkLeft, waving, jumping, failed, waiting, working, reviewing
}

enum CompanionPetAnimationPolicy {
    static func task(_ agent: AgentPresence?, celebrating: Bool) -> CompanionPetAnimation {
        guard let agent else { return .idle }
        switch agent.status {
        case "waiting": return .waiting
        case "error", "failed", "stopped", "cancelled": return .failed
        case "completed": return celebrating ? .jumping : .idle
        case "running", "starting", "queued": break
        default: return .idle
        }
        if agent.activity?.state == "waiting" { return .waiting }
        if agent.activity?.state == "error" { return .failed }
        switch agent.activity?.kind {
        case "read", "search", "web", "review": return .reviewing
        case "edit", "write", "command", "thinking", "agent", "turn": return .working
        default:
            // Legacy hosts can only supply a summary. Never infer waiting or
            // failure from text when the real lifecycle says it is running.
            let summary = agent.summary.lowercased()
            return ["read", "search", "review", "inspect", "읽", "검색", "검토", "찾는"].contains(where: summary.contains) ? .reviewing : .working
        }
    }
}

@MainActor
final class CompanionPetMotion: ObservableObject {
    @Published private(set) var direction: CompanionPetAnimation?
    @Published private(set) var isDragging = false
    private var resetTask: Task<Void, Never>?
    private var generation: UInt64 = 0
    private let idleDelay: Duration
    init(idleDelay: Duration = .milliseconds(350)) { self.idleDelay = idleDelay }
    deinit { resetTask?.cancel() }
    func begin() { end() }
    func move(horizontalDelta: CGFloat) {
        generation &+= 1
        if !isDragging { isDragging = true }
        guard horizontalDelta.isFinite, abs(horizontalDelta) >= 0.5 else { return }
        let next: CompanionPetAnimation = horizontalDelta > 0 ? .walkRight : .walkLeft
        if direction != next { direction = next }
        resetTask?.cancel()
        resetTask = Task { [weak self, idleDelay] in
            do { try await Task.sleep(for: idleDelay) } catch { return }
            guard let self, !Task.isCancelled else { return }
            // Keep drag ownership until release. A pause must not become a tap.
            if self.direction != nil { self.direction = nil }
        }
    }
    func end() {
        generation &+= 1
        resetTask?.cancel(); resetTask = nil
        if direction != nil { direction = nil }
        if isDragging { isDragging = false }
    }
    func endAfterTeardown() {
        resetTask?.cancel(); resetTask = nil
        let expected = generation
        // A representable can be dismantled while SwiftUI is invalidating its
        // graph. Publishing here would reenter that exclusive mutation.
        DispatchQueue.main.async { [weak self] in
            guard let self, self.generation == expected else { return }
            self.end()
        }
    }
}

/// The SwiftUI button still provides accessibility and its context menu. Only
/// left-pointer gestures in the pet image are owned by this native monitor.
struct CompanionPetInteraction: NSViewRepresentable {
    @ObservedObject var motion: CompanionPetMotion
    let row: Int
    let onClick: () -> Void
    func makeNSView(context: Context) -> CompanionPetInteractionProbe {
        let view = CompanionPetInteractionProbe(); view.setAccessibilityElement(false); return view
    }
    func updateNSView(_ view: CompanionPetInteractionProbe, context: Context) {
        view.motion = motion; view.renderedRow = row; view.onClick = onClick
        view.install()
    }
    static func dismantleNSView(_ view: CompanionPetInteractionProbe, coordinator: ()) { view.dispose() }
    static func cancel(in view: NSView?) {
        guard let view else { return }
        if let probe = view as? CompanionPetInteractionProbe { probe.cancel() }
        for child in view.subviews { cancel(in: child) }
    }
}

@MainActor
final class CompanionPetInteractionProbe: NSView {
    var motion: CompanionPetMotion?
    var renderedRow = 0
    var onClick: () -> Void = {}
    private(set) var clickCount = 0
    private(set) var dragCount = 0
    private var receivedEvents = 0
    private var lastEvent: [String: String] = [:]
    private var lastCancellation = "none"
    var diagnostic: [String: String] {
        lastEvent.merging(["receivedEvents": String(receivedEvents), "monitorInstalled": String(monitor != nil),
            "window": String(window?.windowNumber ?? 0), "windowVisible": String(window?.isVisible ?? false),
            "bounds": NSStringFromRect(bounds), "visibleRect": NSStringFromRect(visibleRect),
            "windowFrame": NSStringFromRect(convert(bounds, to: nil)), "hidden": String(isHiddenOrHasHiddenAncestor),
            "gestureActive": String(gesture != nil), "lastCancellation": lastCancellation]) { _, fresh in fresh }
    }
    private struct Gesture {
        let start: NSPoint
        let origin: NSPoint
        var previous: NSPoint
        var dragging = false
    }
    private var gesture: Gesture?
    private var monitor: Any?
    private var observers: [NSObjectProtocol] = []
    override var mouseDownCanMoveWindow: Bool { false }
    override func hitTest(_ point: NSPoint) -> NSView? { nil }
    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow(); removeMonitoring(reason: "window-change")
        if window != nil { install() }
    }
    func install() {
        guard monitor == nil, let window else { return }
        monitor = NSEvent.addLocalMonitorForEvents(matching: [.leftMouseDown, .leftMouseDragged, .leftMouseUp, .keyDown]) { [weak self] event in
            guard let self else { return event }
            return self.handle(event)
        }
        for (name, object) in [(NSWindow.willCloseNotification, window as AnyObject), (NSWindow.didResignKeyNotification, window as AnyObject), (NSApplication.didResignActiveNotification, NSApp as AnyObject)] {
            observers.append(NotificationCenter.default.addObserver(forName: name, object: object, queue: .main) { [weak self] _ in
                MainActor.assumeIsolated { self?.cancel(reason: name.rawValue) }
            })
        }
    }
    private func removeMonitoring(reason: String = "dispose") {
        if let monitor { NSEvent.removeMonitor(monitor); self.monitor = nil }
        observers.forEach { NotificationCenter.default.removeObserver($0) }; observers.removeAll()
        lastCancellation = reason; gesture = nil
        motion?.endAfterTeardown()
    }
    func dispose() { removeMonitoring(); onClick = {}; motion = nil }
    func cancel(reason: String = "explicit") { lastCancellation = reason; gesture = nil; motion?.end() }
    private func handle(_ event: NSEvent) -> NSEvent? {
        if event.window === window || gesture != nil {
            receivedEvents += 1
            lastEvent = ["eventType": String(event.type.rawValue), "eventWindow": String(event.windowNumber),
                "eventPoint": NSStringFromPoint(event.locationInWindow), "screenPoint": NSStringFromPoint(screenPoint(event)),
                "localPoint": NSStringFromPoint(convert(event.locationInWindow, from: nil))]
        }
        guard let window, window.isVisible, !isHiddenOrHasHiddenAncestor else { cancel(reason: "not-visible"); return event }
        if var active = gesture {
            if event.type == .keyDown, event.keyCode == 53 { cancel(); return nil }
            if event.type == .leftMouseDragged {
                let point = screenPoint(event)
                let delta = NSPoint(x: point.x - active.start.x, y: point.y - active.start.y)
                if !active.dragging, hypot(delta.x, delta.y) >= 4 { active.dragging = true; dragCount += 1 }
                if active.dragging {
                    motion?.move(horizontalDelta: point.x - active.previous.x)
                    window.setFrameOrigin(NSPoint(x: active.origin.x + delta.x, y: active.origin.y + delta.y))
                }
                active.previous = point; gesture = active
                return nil
            }
            if event.type == .leftMouseUp {
                let local = convert(window.convertPoint(fromScreen: screenPoint(event)), from: nil)
                let activate = !active.dragging && bounds.intersection(visibleRect).contains(local)
                cancel()
                if activate { clickCount += 1; onClick() }
                return nil
            }
            if event.type == .leftMouseDown { cancel() }
        }
        guard event.type == .leftMouseDown, event.window === window,
              !event.modifierFlags.contains(.control), window.attachedSheet == nil,
              bounds.intersection(visibleRect).contains(convert(event.locationInWindow, from: nil)) else { return event }
        let point = screenPoint(event)
        motion?.begin()
        gesture = Gesture(start: point, origin: window.frame.origin, previous: point)
        return nil
    }
    private func screenPoint(_ event: NSEvent) -> NSPoint {
        event.window?.convertPoint(toScreen: event.locationInWindow) ?? NSEvent.mouseLocation
    }
}
