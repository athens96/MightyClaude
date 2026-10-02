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

    func drag(at position: CGPoint, displayId: UInt32, phase: ScreenShareDragPhase) async {
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
struct AlertScreenShareControlKeyConfirmer: ScreenShareControlKeyConfirmer {
    let mobileRemote: MobileRemoteService

    func confirmControlKey(deviceId: String, fingerprint: String) async -> Bool {
        let device = await mobileRemote.deviceName(deviceId) ?? deviceId
        return await MainActor.run {
            let alert = NSAlert()
            alert.messageText = L("screenShare.controlKey.confirmTitle")
            alert.informativeText = L("screenShare.controlKey.confirmBody",
                                      ["device": device, "fingerprint": fingerprint])
            alert.alertStyle = .informational
            alert.addButton(withTitle: L("screenShare.controlKey.confirmAccept"))
            alert.addButton(withTitle: L("screenShare.controlKey.confirmReject"))
            return alert.runModal() == .alertFirstButtonReturn
        }
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

    private func removeItem() {
        if let item { NSStatusBar.system.removeStatusItem(item) }
        item = nil
    }

    // MARK: Kill-switch hot key

    private func registerHotKey() {
        guard hotKeyRef == nil else { return }
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
            NSLog("screen-share kill hot key handler not installed: %d", installed)
            return
        }
        var ref: EventHotKeyRef?
        let registered = RegisterEventHotKey(UInt32(hotkey.keyCode), Self.carbonModifiers(hotkey.modifiers),
                                             Self.hotKeyID, GetApplicationEventTarget(), 0, &ref)
        if registered == noErr {
            hotKeyRef = ref
        } else {
            // Another app holds ⌃⌥⌘K; the menu-bar item still stops every session.
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

    /// The menu command and the hotkey both land here.
    func stopScreenShareSessions() { screenShareMenuBar?.killNow() }
}
