import AppKit
import Carbon
import CoreGraphics
import MightyCore
import UserNotifications

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
    func move(to position: CGPoint, displayId: UInt32) {
        post(CGEvent(mouseEventSource: nil, mouseType: .mouseMoved, mouseCursorPosition: position, mouseButton: .left))
    }

    func drag(at position: CGPoint, displayId: UInt32, phase: ScreenShareDragPhase) {
        let type: CGEventType
        switch phase {
        case .begin: type = .leftMouseDown
        case .move: type = .leftMouseDragged
        case .end: type = .leftMouseUp
        }
        let event = CGEvent(mouseEventSource: nil, mouseType: type, mouseCursorPosition: position, mouseButton: .left)
        event?.setIntegerValueField(.mouseEventClickState, value: 1)
        post(event)
    }

    func click(at position: CGPoint, displayId: UInt32, button: ScreenShareMouseButton, clickCount: Int) {
        let mouseButton: CGMouseButton = button == .right ? .right : .left
        let down: CGEventType = button == .right ? .rightMouseDown : .leftMouseDown
        let up: CGEventType = button == .right ? .rightMouseUp : .leftMouseUp
        for event in [CGEvent(mouseEventSource: nil, mouseType: down, mouseCursorPosition: position, mouseButton: mouseButton),
                      CGEvent(mouseEventSource: nil, mouseType: up, mouseCursorPosition: position, mouseButton: mouseButton)] {
            event?.setIntegerValueField(.mouseEventClickState, value: Int64(clickCount))
            post(event)
        }
    }

    func scroll(at position: CGPoint, displayId: UInt32, deltaX: Int32, deltaY: Int32) {
        post(CGEvent(mouseEventSource: nil, mouseType: .mouseMoved, mouseCursorPosition: position, mouseButton: .left))
        post(CGEvent(scrollWheelEvent2Source: nil, units: .line, wheelCount: 2,
                     wheel1: deltaY, wheel2: deltaX, wheel3: 0))
    }

    func commitText(_ text: String) {
        // `CGEventKeyboardSetUnicodeString` carries at most 20 UTF-16 units per
        // event; the chunks never split a Hangul syllable or an emoji.
        for chunk in ScreenShareTextChunks.split(text) {
            for down in [true, false] {
                guard let event = CGEvent(keyboardEventSource: nil, virtualKey: 0, keyDown: down) else { continue }
                event.keyboardSetUnicodeString(stringLength: chunk.count, unicodeString: chunk)
                post(event)
            }
        }
    }

    func key(code: UInt16, modifiers: ScreenShareModifiers) {
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

    private func post(_ event: CGEvent?) {
        guard let event else { return }
        // Stamped so the local-HID monitor does not take the phone's own input
        // for a person at the Mac and pause remote input for 2 s.
        event.setIntegerValueField(.eventSourceUserData, value: ScreenShareInjectionTag.marker)
        event.post(tap: .cghidEventTap)
    }
}

/// The Mac pasteboard behind the clipboard buttons. Both directions are manual:
/// nothing is read or written unless the person at the phone pressed a button and
/// the Mac admitted it.
struct SystemScreenSharePasteboard: ScreenSharePasteboard {
    /// Types a password manager marks so nothing syncs them. They are never read.
    static let concealedTypes: [NSPasteboard.PasteboardType] = [
        NSPasteboard.PasteboardType("org.nspasteboard.ConcealedType"),
        NSPasteboard.PasteboardType("com.agilebits.onepassword"),
    ]

    func read() -> ScreenSharePasteboardRead {
        let pasteboard = NSPasteboard.general
        let types = pasteboard.types ?? []
        if Self.concealedTypes.contains(where: types.contains) {
            return ScreenSharePasteboardRead(text: nil, concealed: true)
        }
        guard let text = pasteboard.string(forType: .string), !text.isEmpty else { return .empty }
        return ScreenSharePasteboardRead(text: text)
    }

    func write(_ text: String) {
        let pasteboard = NSPasteboard.general
        pasteboard.clearContents()
        pasteboard.setString(text, forType: .string)
    }
}

/// Asks the person at the Mac to confirm a control key's fingerprint.
///
/// The Mac never silently accepts a key: granting control is the user's decision,
/// and so is accepting the key that will stand for it. A phone that sends a
/// second, different key is refused outright — the old one has to be withdrawn in
/// Settings first.
///
/// The question is a floating window, not an app-modal alert: the Settings stop
/// buttons, the menu-bar kill switch and ⌃⌥⌘K all keep working while it is up.
/// Reject is the default (Return); Register has no key equivalent and takes a
/// click — and the host refuses every remote event while the window is open, so
/// that click can only come from the person at the Mac.
struct AlertScreenShareControlKeyConfirmer: ScreenShareControlKeyConfirmer {
    let mobileRemote: MobileRemoteService

    func confirmControlKey(deviceId: String, fingerprint: String) async -> Bool {
        let device = await mobileRemote.deviceName(deviceId) ?? deviceId
        return await ScreenShareControlKeyPrompt.ask(device: device, fingerprint: fingerprint)
    }
}

/// One control-key question on screen, answered exactly once.
@MainActor
private final class ScreenShareControlKeyPrompt: NSObject {
    /// Prompts on screen right now; each keeps itself alive until answered.
    private static var open: Set<ScreenShareControlKeyPrompt> = []
    private let alert = NSAlert()
    private var answer: CheckedContinuation<Bool, Never>?

    static func ask(device: String, fingerprint: String) async -> Bool {
        let prompt = ScreenShareControlKeyPrompt()
        open.insert(prompt)
        defer { open.remove(prompt) }
        return await withCheckedContinuation { answer in
            // The continuation's body runs right here, on the main thread.
            MainActor.assumeIsolated { prompt.show(device: device, fingerprint: fingerprint, answer: answer) }
        }
    }

    private func show(device: String, fingerprint: String, answer: CheckedContinuation<Bool, Never>) {
        self.answer = answer
        alert.messageText = L("screenShare.controlKey.confirmTitle")
        alert.informativeText = L("screenShare.controlKey.confirmBody", ["device": device, "fingerprint": fingerprint])
        alert.alertStyle = .informational
        // The first button is the default one: Reject answers Return.
        let reject = alert.addButton(withTitle: L("screenShare.controlKey.confirmReject"))
        let register = alert.addButton(withTitle: L("screenShare.controlKey.confirmAccept"))
        reject.keyEquivalent = "\r"
        register.keyEquivalent = ""
        // Not run modally, so the buttons answer here instead of ending a modal session.
        for (button, accepted) in [(reject, false), (register, true)] {
            button.tag = accepted ? 1 : 0
            button.target = self
            button.action = #selector(pressed(_:))
        }
        alert.layout()
        let window = alert.window
        window.level = .floating
        window.center()
        NSApp.activate()
        window.makeKeyAndOrderFront(nil)
    }

    @objc private func pressed(_ sender: NSButton) {
        alert.window.orderOut(nil)
        let answer = self.answer
        self.answer = nil
        answer?.resume(returning: sender.tag == 1)
    }
}

/// The menu-bar item the user stops a session from, and the kill-switch hotkey.
///
/// The item only exists while a session is live — an idle Mac shows nothing —
/// and both the menu action and ⌃⌥⌘K go to the same place: the host's kill
/// switch, which stops capture, injection and the PeerConnection inside 1 s
/// whether the relay is up or not. The hotkey is a Carbon hot key, which needs
/// no Accessibility or Input Monitoring permission and fires whichever app is in
/// front. Event monitors report local keyboard and mouse activity, which pauses
/// remote input for 2 s.
@MainActor
final class ScreenShareMenuBarController {
    private let service: ScreenShareService
    private var item: NSStatusItem?
    private var state = ScreenShareIndicatorState(sessions: [], controlling: false, framesPaused: false)
    private var localMonitor: Any?
    private var globalMonitor: Any?
    private var environmentTimer: Timer?
    private let hotkey = ScreenShareKillHotkey.standard
    private var hotKeyRef: EventHotKeyRef?
    private var hotKeyHandler: EventHandlerRef?
    /// 'MCsk' — tells this app's hot key apart from any other registered one.
    private static let hotKeyID = EventHotKeyID(signature: 0x4D43_736B, id: 1)

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
        unregisterHotKey()
        removeItem()
    }

    /// Both the menu action and the hotkey end here.
    func killNow() {
        Task { await service.killSwitch() }
    }

    // MARK: Indicator

    private func apply(_ state: ScreenShareIndicatorState) {
        let known = Set(self.state.sessions.map(\.sessionId))
        for session in state.sessions where !known.contains(session.sessionId) { announce(session) }
        self.state = state
        // ⌃⌥⌘K belongs to screen sharing only while screen sharing is happening.
        // Holding it on an idle Mac would take the chord away from every other
        // app for a kill switch with nothing to kill.
        if ScreenShareKillHotkey.isRegistered(for: state) { registerHotKey() } else { unregisterHotKey() }
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

    /// One notice per session that opens, so remote access never starts unseen
    /// by someone who is not looking at the menu bar. Nothing is asked here: the
    /// permission is requested from the onboarding screen's System Settings link.
    private func announce(_ session: ScreenShareLiveSession) {
        let control = session.mode == .control
        Task {
            let center = UNUserNotificationCenter.current()
            guard (await center.notificationSettings()).authorizationStatus == .authorized else { return }
            let content = UNMutableNotificationContent()
            content.title = L("screenShare.notification.title")
            content.body = control ? L("screenShare.notification.controlBody") : L("screenShare.notification.viewBody")
            try? await center.add(UNNotificationRequest(identifier: "screen-share-" + session.sessionId,
                                                        content: content, trigger: nil))
        }
    }

    private func removeItem() {
        if let item { NSStatusBar.system.removeStatusItem(item) }
        item = nil
    }

    // MARK: Kill-switch hot key

    private func registerHotKey() {
        guard hotKeyRef == nil else { return }
        // One handler at most: a registration that failed earlier removed its
        // own, so a retry never stacks a second one behind it.
        if hotKeyHandler == nil {
            var pressed = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
            let context = Unmanaged.passUnretained(self).toOpaque()
            let installed = InstallEventHandler(GetApplicationEventTarget(), { _, event, context in
                guard let event, let context else { return OSStatus(eventNotHandledErr) }
                var id = EventHotKeyID()
                let read = GetEventParameter(event, EventParamName(kEventParamDirectObject), EventParamType(typeEventHotKeyID),
                                             nil, MemoryLayout<EventHotKeyID>.size, nil, &id)
                guard read == noErr, id.signature == ScreenShareMenuBarController.hotKeyID.signature,
                      id.id == ScreenShareMenuBarController.hotKeyID.id else { return OSStatus(eventNotHandledErr) }
                let controller = Unmanaged<ScreenShareMenuBarController>.fromOpaque(context).takeUnretainedValue()
                // Carbon delivers application-target events on the main thread.
                MainActor.assumeIsolated { controller.killNow() }
                return noErr
            }, 1, &pressed, context, &hotKeyHandler)
            guard installed == noErr else {
                hotKeyHandler = nil
                NSLog("screen-share kill hot key handler not installed: %d", installed)
                return
            }
        }
        var ref: EventHotKeyRef?
        let registered = RegisterEventHotKey(UInt32(hotkey.keyCode), Self.carbonModifiers(hotkey.modifiers),
                                             Self.hotKeyID, GetApplicationEventTarget(), 0, &ref)
        if registered == noErr {
            hotKeyRef = ref
        } else {
            // Another app holds ⌃⌥⌘K; the menu-bar item still stops every session.
            if let hotKeyHandler { RemoveEventHandler(hotKeyHandler) }
            hotKeyHandler = nil
            NSLog("screen-share kill hot key not registered: %d", registered)
        }
    }

    private func unregisterHotKey() {
        if let hotKeyRef { UnregisterEventHotKey(hotKeyRef) }
        if let hotKeyHandler { RemoveEventHandler(hotKeyHandler) }
        hotKeyRef = nil; hotKeyHandler = nil
    }

    private static func carbonModifiers(_ modifiers: ScreenShareModifiers) -> UInt32 {
        var flags = 0
        if modifiers.contains(.command) { flags |= cmdKey }
        if modifiers.contains(.shift) { flags |= shiftKey }
        if modifiers.contains(.option) { flags |= optionKey }
        if modifiers.contains(.control) { flags |= controlKey }
        return UInt32(flags)
    }

    // MARK: Monitors

    private func installMonitors() {
        let types: NSEvent.EventTypeMask = [.keyDown, .leftMouseDown, .rightMouseDown, .mouseMoved, .scrollWheel]
        localMonitor = NSEvent.addLocalMonitorForEvents(matching: types) { [weak self] event in
            self?.handle(event)
            return event
        }
        // Local HID activity while the app is in the background still has to
        // pause remote input. The kill switch does not depend on these monitors.
        globalMonitor = NSEvent.addGlobalMonitorForEvents(matching: types) { [weak self] event in
            self?.handle(event)
        }
    }

    /// Local keyboard or mouse activity pauses remote input while a session is live.
    ///
    /// An event this app injected itself is not local activity: it carries the
    /// marker `SystemScreenShareInput` stamps on everything it posts, and it is
    /// ignored here. Otherwise the phone's own typing would pause the phone.
    private func handle(_ event: NSEvent) {
        guard state.isActive else { return }
        if let cgEvent = event.cgEvent,
           !ScreenShareInjectionTag.isLocalActivity(eventSourceUserData: cgEvent.getIntegerValueField(.eventSourceUserData)) {
            return
        }
        Task { await service.localHIDActivity() }
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
        let backend = SCStreamCaptureBackend()
        let capture = ScreenShareCaptureController(backend: backend, displays: CoreGraphicsDisplaySource())
        // The engine exists only where WebRTC does, and only once it exists does
        // the Mac advertise `screenShare` to phones.
        let engine = ScreenShareEngine(
            service: screenShare,
            peers: WebRTCScreenSharePeerFactory(),
            capture: capture,
            signals: mobileRemote,
            turn: mobileRemote,
            displays: CoreGraphicsDisplaySource(),
            pasteboard: SystemScreenSharePasteboard(),
            confirmer: AlertScreenShareControlKeyConfirmer(mobileRemote: mobileRemote),
            tapMarker: ScreenShareTapMarkerOverlay(),
            compressor: ZstdScreenShareCompressor())
        screenShareEngine = engine
        backend.setHandlers(
            frame: { frame, status, dirtyRects in
                await engine.deliver(frame: frame, status: status, dirtyRects: dirtyRects)
            },
            interrupted: { await engine.captureInterrupted() })
        Task { [screenShare, mobileRemote] in
            await mobileRemote.attachScreenShare(screenShare)
            await mobileRemote.attachScreenShareEngine(engine)
            // The host decides; the engine only closes its peers and tells the
            // phone what already happened.
            await screenShare.observeStops { stopped in await engine.hostStopped(stopped) }
            await screenShare.observeFrameBlock { blocked, reason in
                await engine.framesBlockedChanged(blocked, reason: reason)
            }
            await screenShare.refreshEnvironment()
        }
    }

    /// Settings: puts one phone on or off the screen-share allow-list.
    func setScreenShareAllowed(deviceId: String, allowed: Bool) {
        Task { [screenShareEngine] in
            do {
                try await screenShare.setAllowed(deviceId: deviceId, allowed: allowed)
                await screenShareEngine?.pushGrant(to: deviceId)
            } catch { self.error = error.localizedDescription }
        }
    }

    /// Settings: changes one phone's grant. A downgrade stops its session.
    func setScreenShareGrant(deviceId: String, grant: ScreenShareGrant, controlKeyPublicData: Data? = nil) {
        Task { [screenShareEngine] in
            do {
                try await screenShare.setGrant(deviceId: deviceId, grant: grant,
                                              controlKeyPublicData: controlKeyPublicData)
                await screenShareEngine?.pushGrant(to: deviceId)
            } catch { self.error = error.localizedDescription }
        }
    }

    /// Settings: forgets one phone's enrolled control key. The host keeps no
    /// separate "forget the key" path — withdrawing control is what clears it —
    /// so this steps the grant down to view and back up to control, in order.
    /// The next time the phone takes control it registers the key it already
    /// holds again, and the person at this Mac has to confirm it again.
    func removeScreenShareControlKey(deviceId: String) {
        Task { [screenShareEngine] in
            do {
                try await screenShare.setGrant(deviceId: deviceId, grant: .view)
                try await screenShare.setGrant(deviceId: deviceId, grant: .control)
                await screenShareEngine?.pushGrant(to: deviceId)
            } catch { self.error = error.localizedDescription }
        }
    }

    /// Settings: ends one live session. The phone hears the same note the menu
    /// bar's kill switch sends.
    func stopScreenShareSession(sessionId: String) {
        Task { await screenShare.endSession(sessionId: sessionId, reason: .killSwitch) }
    }

    /// Settings: plays the measurement reference scene on the main display and
    /// tells connected phones as each phase starts.
    func playScreenShareReferenceScene() {
        ScreenShareReferenceSceneWindow.play { [weak self] phase in
            guard let engine = self?.screenShareEngine else { return }
            Task { await engine.announceScene(phase) }
        }
    }

    /// The menu command and the hotkey both land here.
    func stopScreenShareSessions() { screenShareMenuBar?.killNow() }
}
