import AppKit
import Carbon
import CoreGraphics
import MightyCore

/// The Mac's own answer to "may a remote phone see this screen, and may it type
/// here right now": the lock screen and secure input (a password field, a
/// terminal reading a password) both stop frames and refuse injection.
struct SystemScreenShareEnvironment: ScreenShareEnvironmentProbe {
    func screenLocked() -> Bool {
        guard let session = CGSessionCopyCurrentDictionary() as? [String: Any] else { return false }
        // Not on the console (fast user switching) counts as locked too: the
        // phone must not see another user's session.
        if let onConsole = session["kCGSSessionOnConsoleKey"] as? Bool, !onConsole { return true }
        return session["CGSSessionScreenIsLocked"] as? Bool ?? false
    }

    func secureInputActive() -> Bool { IsSecureEventInputEnabled() }
}

/// Remote input, posted with `CGEventPost`. Korean arrives as committed text and
/// is injected as a unicode string rather than as jamo keystrokes, so no input
/// method has to be driven from the outside.
struct SystemScreenShareInput: ScreenShareInputSink {
    func move(to position: CGPoint, displayId: UInt32) async {
        post(CGEvent(mouseEventSource: nil, mouseType: .mouseMoved, mouseCursorPosition: position, mouseButton: .left))
    }

    func click(at position: CGPoint, displayId: UInt32, button: ScreenShareMouseButton, clickCount: Int) async {
        let mouseButton: CGMouseButton = button == .right ? .right : .left
        let down: CGEventType = button == .right ? .rightMouseDown : .leftMouseDown
        let up: CGEventType = button == .right ? .rightMouseUp : .leftMouseUp
        for event in [CGEvent(mouseEventSource: nil, mouseType: down, mouseCursorPosition: position, mouseButton: mouseButton),
                      CGEvent(mouseEventSource: nil, mouseType: up, mouseCursorPosition: position, mouseButton: mouseButton)] {
            event?.setIntegerValueField(.mouseEventClickState, value: Int64(clickCount))
            post(event)
        }
    }

    func scroll(at position: CGPoint, displayId: UInt32, deltaX: Int32, deltaY: Int32) async {
        post(CGEvent(mouseEventSource: nil, mouseType: .mouseMoved, mouseCursorPosition: position, mouseButton: .left))
        post(CGEvent(scrollWheelEvent2Source: nil, units: .line, wheelCount: 2,
                     wheel1: deltaY, wheel2: deltaX, wheel3: 0))
    }

    func commitText(_ text: String) async {
        // One event per grapheme-safe chunk: the whole string in one event is
        // truncated by the window server past 20 UTF-16 units.
        var scalars = Array(text.utf16)
        while !scalars.isEmpty {
            let chunk = Array(scalars.prefix(16))
            scalars.removeFirst(chunk.count)
            for type in [true, false] {
                guard let event = CGEvent(keyboardEventSource: nil, virtualKey: 0, keyDown: type) else { continue }
                event.keyboardSetUnicodeString(stringLength: chunk.count, unicodeString: chunk)
                post(event)
            }
        }
    }

    func key(code: UInt16, modifiers: ScreenShareModifiers) async {
        var flags: CGEventFlags = []
        if modifiers.contains(.command) { flags.insert(.maskCommand) }
        if modifiers.contains(.shift) { flags.insert(.maskShift) }
        if modifiers.contains(.option) { flags.insert(.maskAlternate) }
        if modifiers.contains(.control) { flags.insert(.maskControl) }
        for down in [true, false] {
            guard let event = CGEvent(keyboardEventSource: nil, virtualKey: CGKeyCode(code), keyDown: down) else { continue }
            event.flags = flags
            post(event)
        }
    }

    private func post(_ event: CGEvent?) { event?.post(tap: .cghidEventTap) }
}

/// The menu-bar item the user stops a session from, and the kill-switch hotkey.
///
/// The item only exists while a session is live — an idle Mac shows nothing —
/// and both the menu action and ⌃⌥⌘K go to the same place: the host's kill
/// switch, which stops capture, injection and the PeerConnection inside 1 s
/// whether the relay is up or not. The same monitors report local keyboard and
/// mouse activity, which pauses remote input for 2 s.
@MainActor
final class ScreenShareMenuBarController {
    private let service: ScreenShareService
    private var item: NSStatusItem?
    private var state = ScreenShareIndicatorState(sessions: [], controlling: false, framesPaused: false)
    private var localMonitor: Any?
    private var globalMonitor: Any?
    private var environmentTimer: Timer?
    private let hotkey = ScreenShareKillHotkey.standard

    init(service: ScreenShareService) { self.service = service }

    func start() {
        installMonitors()
        // The lock screen and secure input are polled: neither posts a
        // notification an unprivileged app may rely on.
        let timer = Timer(timeInterval: 1, repeats: true) { [weak self] _ in
            guard let self else { return }
            Task { await self.service.refreshEnvironment() }
        }
        RunLoop.main.add(timer, forMode: .common)
        environmentTimer = timer
        Task { [weak self] in
            guard let self else { return }
            await service.observeIndicator { [weak self] state in
                Task { @MainActor in self?.apply(state) }
            }
        }
    }

    func stop() {
        environmentTimer?.invalidate(); environmentTimer = nil
        if let localMonitor { NSEvent.removeMonitor(localMonitor) }
        if let globalMonitor { NSEvent.removeMonitor(globalMonitor) }
        localMonitor = nil; globalMonitor = nil
        removeItem()
    }

    /// Both the menu action and the hotkey end here.
    func killNow() {
        Task { await service.killSwitch() }
    }

    // MARK: Indicator

    private func apply(_ state: ScreenShareIndicatorState) {
        self.state = state
        guard state.isActive else { removeItem(); return }
        let item = self.item ?? NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        self.item = item
        let title = state.controlling ? L("screenShare.indicator.control") : L("screenShare.indicator.view")
        item.button?.title = state.framesPaused ? L("screenShare.indicator.paused") : title
        item.button?.image = NSImage(systemSymbolName: state.controlling ? "display.and.arrow.down" : "eye",
                                    accessibilityDescription: title)
        item.button?.imagePosition = .imageLeading
        item.menu = menu(title: title)
    }

    private func menu(title: String) -> NSMenu {
        let menu = NSMenu()
        let header = NSMenuItem(title: title, action: nil, keyEquivalent: "")
        header.isEnabled = false
        menu.addItem(header)
        menu.addItem(NSMenuItem(
            title: L("screenShare.indicator.count", ["count": String(state.sessions.count)]),
            action: nil, keyEquivalent: ""))
        menu.addItem(.separator())
        let stop = NSMenuItem(title: L("screenShare.indicator.stop"), action: #selector(stopPressed), keyEquivalent: "k")
        stop.keyEquivalentModifierMask = [.control, .option, .command]
        stop.target = self
        menu.addItem(stop)
        return menu
    }

    @objc private func stopPressed() { killNow() }

    private func removeItem() {
        if let item { NSStatusBar.system.removeStatusItem(item) }
        item = nil
    }

    // MARK: Monitors

    private func installMonitors() {
        let types: NSEvent.EventTypeMask = [.keyDown, .leftMouseDown, .rightMouseDown, .mouseMoved, .scrollWheel]
        localMonitor = NSEvent.addLocalMonitorForEvents(matching: types) { [weak self] event in
            guard let self else { return event }
            if self.handle(event) { return nil }
            return event
        }
        // Local HID activity while the app is in the background still has to
        // pause remote input, and the kill switch has to work from anywhere.
        globalMonitor = NSEvent.addGlobalMonitorForEvents(matching: types) { [weak self] event in
            _ = self?.handle(event)
        }
    }

    /// Returns true when the event was the kill-switch hotkey and must not
    /// travel any further.
    private func handle(_ event: NSEvent) -> Bool {
        if event.type == .keyDown, hotkey.matches(keyCode: event.keyCode, modifiers: Self.modifiers(event)) {
            killNow()
            return true
        }
        guard state.isActive else { return false }
        Task { await service.localHIDActivity() }
        return false
    }

    private static func modifiers(_ event: NSEvent) -> ScreenShareModifiers {
        var modifiers: ScreenShareModifiers = []
        let flags = event.modifierFlags
        if flags.contains(.command) { modifiers.insert(.command) }
        if flags.contains(.shift) { modifiers.insert(.shift) }
        if flags.contains(.option) { modifiers.insert(.option) }
        if flags.contains(.control) { modifiers.insert(.control) }
        return modifiers
    }
}

extension AppStore {
    /// Wires screen-share safety into the app: the allow-list on disk, the
    /// menu-bar kill switch and hotkey, and the mobile-remote paths that must
    /// stop a live session — a revoked phone, a regenerated pairing key.
    func configureScreenShare() {
        guard screenShareMenuBar == nil else { return }
        let controller = ScreenShareMenuBarController(service: screenShare)
        screenShareMenuBar = controller
        controller.start()
        Task { [screenShare] in
            await mobileRemote.attachScreenShare(screenShare)
            await screenShare.refreshEnvironment()
        }
    }

    /// Settings: puts one phone on or off the screen-share allow-list.
    func setScreenShareAllowed(deviceId: String, allowed: Bool) {
        Task {
            do { try await screenShare.setAllowed(deviceId: deviceId, allowed: allowed) }
            catch { self.error = error.localizedDescription }
        }
    }

    /// Settings: changes one phone's grant. A downgrade stops its session.
    func setScreenShareGrant(deviceId: String, grant: ScreenShareGrant, controlKeyPublicData: Data? = nil) {
        Task {
            do {
                try await screenShare.setGrant(deviceId: deviceId, grant: grant,
                                              controlKeyPublicData: controlKeyPublicData)
            } catch { self.error = error.localizedDescription }
        }
    }

    /// The menu command and the hotkey both land here.
    func stopScreenShareSessions() { screenShareMenuBar?.killNow() }
}
