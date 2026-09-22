import AppKit
import SwiftUI

@MainActor final class ApplicationFixture {
    var isActive = true
    var keyWindow: NSWindow?
    var modalWindow: NSWindow?
}
@MainActor let NSApp = ApplicationFixture()

@MainActor final class AppStore {
    static weak var fixtureCurrent: AppStore?
    var hasModal = false
    var allowsBegin = true
    var selected: [String] = []
    var renamed: [String] = []
    var draggedPane: PaneDragPayload?
    var cancellations = 0
    var onSelect: (() -> Void)?
    var onBegin: (() -> Void)?
    var onCancel: (() -> Void)?
    var onFinish: (() -> Void)?
    func selectSession(_ id: String) { selected.append(id); onSelect?() }
    func beginRenameSession(_ id: String) { renamed.append(id) }
    func setPaneFocus(_ value: Bool, sessionId: String) {}
    func closeSession(_ id: String) {}
    func beginPaneDrag(_ id: String) -> PaneDragPayload? {
        guard allowsBegin else { return nil }
        let payload = PaneDragPayload(workspaceId: "workspace", sessionId: id, dragId: UUID().uuidString)
        draggedPane = payload
        onBegin?()
        return payload
    }
    func cancelPaneDrag(_ payload: PaneDragPayload) {
        if draggedPane == payload { draggedPane = nil; cancellations += 1 }
        onCancel?()
    }
    func canDropPane(in id: String) -> Bool { !hasModal }
    func finishPaneDrag(_ payload: PaneDragPayload, workspaceId: String, groupId: String,
                        placement: String, beforeSessionId: String?) -> Bool {
        guard draggedPane == payload else { return false }
        draggedPane = nil
        onFinish?()
        return true
    }
}
enum Palette { static let accent = Color.blue }
struct PaneDragPayload: Equatable {
    let workspaceId: String
    let sessionId: String
    let dragId: String
}

@MainActor final class WindowFixture: NSWindow {
    var simulatedKey = true
    var simulatedVisible = true
    var simulatedMiniaturized = false
    var simulatedOnActiveSpace = true
    var simulatedSheet: NSWindow?
    var queuedEvents: [NSEvent] = []
    var dequeuedEvents: [NSEvent] = []
    var reads = 0
    override var isKeyWindow: Bool { simulatedKey }
    override var isVisible: Bool { simulatedVisible }
    override var isMiniaturized: Bool { simulatedMiniaturized }
    override var isOnActiveSpace: Bool { simulatedOnActiveSpace }
    override var attachedSheet: NSWindow? { simulatedSheet }
    override func nextEvent(matching mask: NSEvent.EventTypeMask, until expiration: Date?,
                            inMode mode: RunLoop.Mode, dequeue: Bool) -> NSEvent? {
        reads += 1
        if let index = queuedEvents.firstIndex(where: { mask.contains(NSEvent.EventTypeMask(rawValue: 1 << $0.type.rawValue)) }) {
            let event = queuedEvents[index]
            if dequeue { dequeuedEvents.append(queuedEvents.remove(at: index)) }
            return event
        }
        // An exhausted test queue makes the old loop stop. Never block the
        // test process or read the system application's real event queue.
        simulatedKey = false
        return nil
    }
}

// PRODUCTION_PANE_DOCK

extension PaneDockDragCoordinator {
    var starts: Int { nativeDragStarts }
    var finishes: Int { nativeDragCompletions }
    var cancellations: Int { AppStore.fixtureCurrent?.cancellations ?? 0 }
    var allowsBegin: Bool {
        get { AppStore.fixtureCurrent?.allowsBegin == true }
        set { AppStore.fixtureCurrent?.allowsBegin = newValue }
    }
    func reset() {
        cancel(); nativeDragStarts = 0; nativeDragCompletions = 0
        AppStore.fixtureCurrent?.cancellations = 0
    }
}

extension PaneDockTabHandleView {
    var fixtureGestureActive: Bool { gesture != nil }
    var fixtureResourcesReleased: Bool { gesture == nil && gestureMonitor == nil && gestureObservers.isEmpty && releaseWatch == nil }
    func fixtureRoute(_ event: NSEvent) -> NSEvent? { handleGestureEvent(event) }
    func fixtureCheckRelease() { checkForLostMouseUp() }
}

@MainActor final class Fixture {
    let window = WindowFixture(contentRect: NSRect(x: -4000, y: -4000, width: 300, height: 160),
                               styleMask: [.borderless], backing: .buffered, defer: false)
    let root = NSView(frame: NSRect(x: 0, y: 0, width: 300, height: 160))
    let group = PaneDockGroupAnchorView(frame: NSRect(x: 0, y: 0, width: 300, height: 160))
    let tab = PaneDockTabHandleView(frame: NSRect(x: 0, y: 0, width: 120, height: 28))
    let store = AppStore()
    var buttonDown = true
    var cursorPushes = 0
    var cursorPops = 0
    init() {
        NSApp.isActive = true; NSApp.keyWindow = window; NSApp.modalWindow = nil
        window.contentView = root
        group.workspaceId = "workspace"; group.groupId = "group"
        root.addSubview(group)
        root.addSubview(tab)
        tab.store = store; tab.sessionId = "one"; tab.workspaceId = "workspace"; tab.groupId = "group"
        tab.gestureEnvironment = .init(isLeftMouseButtonDown: { [weak self] in self?.buttonDown == true },
                                      pushCursor: { [weak self] in self?.cursorPushes += 1 },
                                      popCursor: { [weak self] in self?.cursorPops += 1 })
        AppStore.fixtureCurrent = store
        PaneDockDragCoordinator.shared.reset()
        PaneDockDragCoordinator.shared.register(group)
        PaneDockDragCoordinator.shared.register(tab)
    }
    func mouse(_ type: NSEvent.EventType, x: CGFloat = 10, y: CGFloat = 10, clicks: Int = 1) -> NSEvent {
        NSEvent.mouseEvent(with: type, location: NSPoint(x: x, y: y), modifierFlags: [],
                           timestamp: ProcessInfo.processInfo.systemUptime,
                           windowNumber: window.windowNumber, context: nil, eventNumber: 1,
                           clickCount: clicks, pressure: type == .leftMouseUp ? 0 : 1)!
    }
    func key(_ characters: String = "k", code: UInt16 = 40, modifiers: NSEvent.ModifierFlags = []) -> NSEvent {
        NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: modifiers,
                        timestamp: ProcessInfo.processInfo.systemUptime, windowNumber: window.windowNumber,
                        context: nil, characters: characters, charactersIgnoringModifiers: characters,
                        isARepeat: false, keyCode: code)!
    }
    func anotherTab() -> PaneDockTabHandleView {
        let other = PaneDockTabHandleView(frame: NSRect(x: 140, y: 0, width: 120, height: 28))
        other.store = store; other.sessionId = "two"; other.workspaceId = "workspace"; other.groupId = "group"
        other.gestureEnvironment = tab.gestureEnvironment
        root.addSubview(other)
        PaneDockDragCoordinator.shared.register(other)
        return other
    }
    func cleanup() { tab.removeFromSuperview(); window.contentView = nil; NSApp.keyWindow = nil }
}

@main struct PaneInputChecks {
    @MainActor static func main() throws {
        _ = NSApplication.shared
        let realApplicationActive = NSApplication.shared.isActive
        let realKeyWindow = NSApplication.shared.keyWindow
        let frontmostProcess = NSWorkspace.shared.frontmostApplication?.processIdentifier
        let fixture = Fixture()
        let key = fixture.key()
        let copy = fixture.key("c", code: 8, modifiers: .command)
        let nextDown = fixture.mouse(.leftMouseDown, x: 220, y: 90)
        let nextUp = fixture.mouse(.leftMouseUp, x: 220, y: 90)
        fixture.window.queuedEvents = [key, copy, nextDown, nextUp]
        guard fixture.mouse(.leftMouseDown).window === fixture.window else {
            throw NSError(domain: "PaneInputFixture", code: 1,
                          userInfo: [NSLocalizedDescriptionKey: "WindowServer access is required for isolated NSEvent.window resolution; this is not an input regression failure."])
        }
        fixture.tab.mouseDown(with: fixture.mouse(.leftMouseDown))
        var result = [
            "selectsOnPress": fixture.store.selected == ["one"],
            "mouseDownDoesNotDrainEventQueue": fixture.window.reads == 0,
            "printableKeyRemainsAvailable": fixture.window.queuedEvents.contains { $0 === key },
            "copyKeyRemainsAvailable": fixture.window.queuedEvents.contains { $0 === copy },
            "nextClickRetainsBothEvents": fixture.window.queuedEvents.contains { $0 === nextDown }
                && fixture.window.queuedEvents.contains { $0 === nextUp },
        ]
        fixture.cleanup()
        if CommandLine.arguments.last != "legacy" {
            let coordinator = PaneDockDragCoordinator.shared
            func check(_ name: String, _ block: (Fixture) -> Bool) {
                let fixture = Fixture()
                result[name] = block(fixture)
                fixture.cleanup()
            }
            func beginDrag(_ f: Fixture) {
                f.tab.mouseDown(with: f.mouse(.leftMouseDown))
                f.tab.mouseDragged(with: f.mouse(.leftMouseDragged, x: 18))
            }
            func checkCancellation(_ name: String, mutation: (Fixture) -> Void) {
                check(name) { f in
                    beginDrag(f)
                    mutation(f)
                    f.tab.mouseDragged(with: f.mouse(.leftMouseDragged, x: 22))
                    f.tab.mouseUp(with: f.mouse(.leftMouseUp, x: 22))
                    return f.tab.fixtureResourcesReleased && coordinator.starts == 1
                        && coordinator.finishes == 0 && coordinator.cancellations == 1
                        && f.cursorPushes == 1 && f.cursorPops == 1
                }
            }
            check("belowThresholdDoesNotDrag") { f in
                f.tab.mouseDown(with: f.mouse(.leftMouseDown))
                f.tab.mouseDragged(with: f.mouse(.leftMouseDragged, x: 14))
                f.tab.mouseUp(with: f.mouse(.leftMouseUp, x: 14))
                return coordinator.starts == 0 && f.tab.fixtureResourcesReleased && f.cursorPushes == 0
            }
            check("exactThresholdStartsAndCompletesDrag") { f in
                f.tab.mouseDown(with: f.mouse(.leftMouseDown))
                f.tab.mouseDragged(with: f.mouse(.leftMouseDragged, x: 15))
                f.tab.mouseDragged(with: f.mouse(.leftMouseDragged, x: 70))
                let preview = coordinator.target?.groupId == "group"
                f.tab.mouseUp(with: f.mouse(.leftMouseUp, x: 70))
                return coordinator.starts == 1 && preview && coordinator.finishes == 1
                    && f.cursorPushes == 1 && f.cursorPops == 1 && f.tab.fixtureResourcesReleased
            }
            check("plainClickKeepsSelectionWithoutDragging") { f in
                f.tab.mouseDown(with: f.mouse(.leftMouseDown))
                f.tab.mouseUp(with: f.mouse(.leftMouseUp))
                return f.store.selected == ["one"] && f.store.renamed.isEmpty
                    && coordinator.starts == 0 && f.tab.fixtureResourcesReleased
            }
            check("doubleClickRenamesOnRelease") { f in
                f.tab.mouseDown(with: f.mouse(.leftMouseDown, clicks: 2))
                let onlySelected = f.store.renamed.isEmpty
                f.tab.mouseUp(with: f.mouse(.leftMouseUp, clicks: 2))
                return onlySelected && f.store.renamed == ["one"] && f.tab.fixtureResourcesReleased
            }
            check("doubleClickOutsideDoesNotRename") { f in
                f.tab.mouseDown(with: f.mouse(.leftMouseDown, clicks: 2))
                f.tab.mouseUp(with: f.mouse(.leftMouseUp, x: 220))
                return f.store.renamed.isEmpty && f.tab.fixtureResourcesReleased
            }
            check("doubleClickDragDoesNotRename") { f in
                f.tab.mouseDown(with: f.mouse(.leftMouseDown, clicks: 2))
                f.tab.mouseDragged(with: f.mouse(.leftMouseDragged, x: 18))
                f.tab.mouseUp(with: f.mouse(.leftMouseUp))
                return coordinator.finishes == 1 && f.store.renamed.isEmpty
            }
            check("normalKeysAndShortcutsKeepDispatch") { f in
                beginDrag(f)
                let keys = [f.key(), f.key("c", code: 8, modifiers: .command), f.key("z", code: 6, modifiers: .command)]
                return keys.allSatisfy { f.tab.fixtureRoute($0) === $0 } && f.tab.fixtureGestureActive
            }
            check("gestureDoesNotChangeFirstResponder") { f in
                let responder = f.window.firstResponder
                beginDrag(f)
                return f.window.firstResponder === responder
            }
            check("escapeCancelsActiveGestureOnce") { f in
                beginDrag(f)
                let escape = f.key("\u{1b}", code: 53)
                let consumed = f.tab.fixtureRoute(escape) == nil
                let nextEscapesNormally = f.tab.fixtureRoute(escape) === escape
                f.tab.mouseUp(with: f.mouse(.leftMouseUp))
                return consumed && nextEscapesNormally && f.tab.fixtureResourcesReleased
                    && coordinator.cancellations == 1 && coordinator.finishes == 0 && f.cursorPops == 1
            }
            check("escapeInAnotherWindowKeepsDispatch") { f in
                beginDrag(f)
                let other = WindowFixture(contentRect: .zero, styleMask: [.borderless], backing: .buffered, defer: false)
                let escape = NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: [], timestamp: 0,
                                              windowNumber: other.windowNumber, context: nil, characters: "\u{1b}",
                                              charactersIgnoringModifiers: "\u{1b}", isARepeat: false, keyCode: 53)!
                return f.tab.fixtureRoute(escape) === escape && f.tab.fixtureGestureActive
            }
            check("newClickCancelsOldGestureAndKeepsClick") { f in
                beginDrag(f)
                let down = f.mouse(.leftMouseDown, x: 220)
                return f.tab.fixtureRoute(down) === down && f.tab.fixtureResourcesReleased
                    && coordinator.cancellations == 1 && f.cursorPops == 1
            }
            check("contextClickCancelsAndKeepsClick") { f in
                beginDrag(f)
                let down = f.mouse(.rightMouseDown)
                return f.tab.fixtureRoute(down) === down && f.tab.fixtureResourcesReleased
            }
            check("lostMouseUpWatchdogCancels") { f in
                beginDrag(f)
                f.buttonDown = false
                RunLoop.main.run(until: Date(timeIntervalSinceNow: 0.16))
                return f.tab.fixtureResourcesReleased && coordinator.cancellations == 1
                    && coordinator.finishes == 0 && f.cursorPops == 1
            }
            check("heldMouseButtonKeepsGesture") { f in
                beginDrag(f)
                f.tab.fixtureCheckRelease()
                return f.tab.fixtureGestureActive && coordinator.cancellations == 0 && f.cursorPops == 0
            }
            checkCancellation("appResignNotificationCancels") { _ in
                NotificationCenter.default.post(name: NSApplication.didResignActiveNotification, object: NSApp)
            }
            checkCancellation("windowResignNotificationCancels") { f in
                NotificationCenter.default.post(name: NSWindow.didResignKeyNotification, object: f.window)
            }
            checkCancellation("windowCloseNotificationCancels") { f in
                NotificationCenter.default.post(name: NSWindow.willCloseNotification, object: f.window)
            }
            checkCancellation("spaceSwitchNotificationCancels") { _ in
                NSWorkspace.shared.notificationCenter.post(name: NSWorkspace.activeSpaceDidChangeNotification, object: nil)
            }
            checkCancellation("inactiveAppCancels") { _ in NSApp.isActive = false }
            checkCancellation("otherKeyWindowCancels") { _ in NSApp.keyWindow = nil }
            checkCancellation("nonKeyWindowCancels") { $0.window.simulatedKey = false }
            checkCancellation("hiddenWindowCancels") { $0.window.simulatedVisible = false }
            checkCancellation("minimizedWindowCancels") { $0.window.simulatedMiniaturized = true }
            checkCancellation("offSpaceWindowCancels") { $0.window.simulatedOnActiveSpace = false }
            checkCancellation("modalCancels") { NSApp.modalWindow = $0.window }
            checkCancellation("sheetCancels") { $0.window.simulatedSheet = $0.window }
            checkCancellation("storeModalCancels") { $0.store.hasModal = true }
            checkCancellation("hiddenTabCancels") { $0.tab.isHidden = true }
            checkCancellation("hiddenAncestorCancels") { $0.root.isHidden = true }
            checkCancellation("detachedTabCancels") { $0.tab.removeFromSuperview() }
            checkCancellation("sessionRebindCancels") { $0.tab.sessionId = "two" }
            checkCancellation("workspaceRebindCancels") { $0.tab.workspaceId = "other" }
            checkCancellation("groupRebindCancels") { $0.tab.groupId = "other" }
            checkCancellation("storeRebindCancels") { $0.tab.store = nil }
            check("unchangedBindingPreservesGesture") { f in
                beginDrag(f)
                f.tab.store = f.store; f.tab.sessionId = "one"; f.tab.workspaceId = "workspace"; f.tab.groupId = "group"
                return f.tab.fixtureGestureActive && coordinator.cancellations == 0
            }
            check("failedBeginReleasesGestureWithoutCursor") { f in
                coordinator.allowsBegin = false
                beginDrag(f)
                return f.tab.fixtureResourcesReleased && coordinator.starts == 0 && f.cursorPushes == 0 && f.cursorPops == 0
            }
            check("duplicateCancellationIsIdempotent") { f in
                beginDrag(f)
                f.tab.cancelGesture(); f.tab.cancelGesture(); f.tab.mouseUp(with: f.mouse(.leftMouseUp))
                return coordinator.cancellations == 1 && f.cursorPushes == 1 && f.cursorPops == 1
            }
            check("selectionRebindCannotCreateStaleGesture") { f in
                f.store.onSelect = { f.tab.sessionId = "two" }
                f.tab.mouseDown(with: f.mouse(.leftMouseDown))
                return f.store.selected == ["one"] && f.tab.fixtureResourcesReleased
            }
            check("newGestureDuringSelectionWins") { f in
                let other = f.anotherTab()
                f.store.onSelect = {
                    f.store.onSelect = nil
                    other.mouseDown(with: f.mouse(.leftMouseDown, x: 150))
                    other.mouseDragged(with: f.mouse(.leftMouseDragged, x: 160))
                }
                f.tab.mouseDown(with: f.mouse(.leftMouseDown))
                f.tab.mouseDragged(with: f.mouse(.leftMouseDragged, x: 18))
                let preserved = f.tab.fixtureResourcesReleased && other.fixtureGestureActive
                    && f.store.draggedPane?.sessionId == "two"
                other.mouseUp(with: f.mouse(.leftMouseUp, x: 160))
                return preserved && coordinator.finishes == 1
            }
            check("lateOldViewTeardownPreservesNewDrag") { f in
                beginDrag(f)
                let other = f.anotherTab()
                other.mouseDown(with: f.mouse(.leftMouseDown, x: 150))
                other.mouseDragged(with: f.mouse(.leftMouseDragged, x: 160))
                let current = f.store.draggedPane
                f.tab.isHidden = true; f.tab.removeFromSuperview(); f.tab.fixtureCheckRelease()
                f.tab.mouseUp(with: f.mouse(.leftMouseUp)); f.tab.cancelGesture()
                let preserved = other.fixtureGestureActive && f.store.draggedPane == current && current?.sessionId == "two"
                other.mouseUp(with: f.mouse(.leftMouseUp, x: 160))
                return preserved && coordinator.starts == 2 && coordinator.finishes == 1
                    && f.cursorPushes == 2 && f.cursorPops == 2
            }
            check("newGestureDuringCancellationWins") { f in
                beginDrag(f)
                let other = f.anotherTab()
                f.store.onCancel = {
                    f.store.onCancel = nil
                    other.mouseDown(with: f.mouse(.leftMouseDown, x: 150))
                    other.mouseDragged(with: f.mouse(.leftMouseDragged, x: 160))
                }
                f.tab.cancelGesture()
                let preserved = other.fixtureGestureActive && f.store.draggedPane?.sessionId == "two"
                other.mouseUp(with: f.mouse(.leftMouseUp, x: 160))
                return preserved && coordinator.finishes == 1 && f.tab.fixtureResourcesReleased
            }
            check("sameViewRestartDuringCancellationWins") { f in
                beginDrag(f)
                let original = f.store.draggedPane
                f.store.onCancel = {
                    f.store.onCancel = nil
                    beginDrag(f)
                }
                f.tab.cancelGesture()
                let preserved = f.tab.fixtureGestureActive && f.store.draggedPane != nil && f.store.draggedPane != original
                f.tab.mouseUp(with: f.mouse(.leftMouseUp, x: 20))
                return preserved && coordinator.finishes == 1 && f.cursorPushes == 2 && f.cursorPops == 2
            }
            check("newGestureDuringBeginWins") { f in
                let other = f.anotherTab()
                f.store.onBegin = {
                    f.store.onBegin = nil
                    other.mouseDown(with: f.mouse(.leftMouseDown, x: 150))
                    other.mouseDragged(with: f.mouse(.leftMouseDragged, x: 160))
                }
                beginDrag(f)
                let preserved = f.tab.fixtureResourcesReleased && other.fixtureGestureActive
                    && f.store.draggedPane?.sessionId == "two"
                other.mouseUp(with: f.mouse(.leftMouseUp, x: 160))
                return preserved && coordinator.finishes == 1 && f.cursorPushes == 1 && f.cursorPops == 1
            }
            check("newGestureDuringFinishWins") { f in
                beginDrag(f)
                let other = f.anotherTab()
                f.store.onFinish = {
                    f.store.onFinish = nil
                    other.mouseDown(with: f.mouse(.leftMouseDown, x: 150))
                    other.mouseDragged(with: f.mouse(.leftMouseDragged, x: 160))
                }
                f.tab.mouseUp(with: f.mouse(.leftMouseUp, x: 20))
                let preserved = f.tab.fixtureResourcesReleased && other.fixtureGestureActive
                    && f.store.draggedPane?.sessionId == "two"
                other.mouseUp(with: f.mouse(.leftMouseUp, x: 160))
                return preserved && coordinator.finishes == 2 && f.cursorPushes == 2 && f.cursorPops == 2
            }
            check("oldCoordinatorOwnerCannotCancelNewDrag") { f in
                let oldOwner = UUID(), newOwner = UUID()
                _ = coordinator.begin(store: f.store, sessionId: "one", window: f.window, owner: oldOwner)
                _ = coordinator.begin(store: f.store, sessionId: "two", window: f.window, owner: newOwner)
                let current = f.store.draggedPane
                coordinator.cancel(owner: oldOwner)
                coordinator.finish(location: NSPoint(x: 40, y: 10), window: f.window, owner: oldOwner)
                return current != nil && f.store.draggedPane == current && coordinator.finishes == 0
            }
        }
        result["doesNotActivateApplication"] = NSApplication.shared.isActive == realApplicationActive
            && NSWorkspace.shared.frontmostApplication?.processIdentifier == frontmostProcess
        result["doesNotChangeRealKeyWindow"] = NSApplication.shared.keyWindow === realKeyWindow
        let encoded = try JSONSerialization.data(withJSONObject: result, options: [.sortedKeys])
        print(String(decoding: encoded, as: UTF8.self))
    }
}
