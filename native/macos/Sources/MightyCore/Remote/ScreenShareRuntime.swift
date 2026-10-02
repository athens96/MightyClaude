import CoreGraphics
import Foundation

// MARK: - Displays

/// Where display geometry comes from. `CGDisplayBounds` in production; a test
/// passes its own layout, including a display that has just been unplugged.
public protocol ScreenShareDisplaySource: Sendable {
    /// The displays that exist right now, main display first.
    func activeDisplayIds() -> [UInt32]
    /// Global bounds of one display, or nil when it is gone.
    func bounds(of displayId: UInt32) -> CGRect?
    func mainDisplayId() -> UInt32
}

/// The real thing: Quartz display services.
public struct CoreGraphicsDisplaySource: ScreenShareDisplaySource {
    public init() {}

    public func activeDisplayIds() -> [UInt32] {
        var count: UInt32 = 0
        guard CGGetActiveDisplayList(0, nil, &count) == .success, count > 0 else { return [] }
        var ids = [CGDirectDisplayID](repeating: 0, count: Int(count))
        guard CGGetActiveDisplayList(count, &ids, &count) == .success else { return [] }
        let main = CGMainDisplayID()
        return ids.prefix(Int(count)).sorted { left, _ in left == main }.map { UInt32($0) }
    }

    public func bounds(of displayId: UInt32) -> CGRect? {
        // An id no longer attached has no bounds; CGDisplayBounds answers a null
        // rect for it, which must not be mistaken for a 0×0 display at the origin.
        guard CGDisplayIsActive(CGDirectDisplayID(displayId)) != 0 else { return nil }
        let bounds = CGDisplayBounds(CGDirectDisplayID(displayId))
        guard !bounds.isNull, bounds.width > 0, bounds.height > 0 else { return nil }
        return bounds
    }

    public func mainDisplayId() -> UInt32 { UInt32(CGMainDisplayID()) }
}

/// A normalized point resolved against real display bounds.
public struct ScreenShareResolvedPoint: Sendable, Equatable {
    public var displayId: UInt32
    /// Global pixel position inside `CGDisplayBounds(displayId)`.
    public var position: CGPoint
    /// True when the requested display was gone and the main display was used.
    public var fellBackToMain: Bool

    public init(displayId: UInt32, position: CGPoint, fellBackToMain: Bool) {
        self.displayId = displayId; self.position = position; self.fellBackToMain = fellBackToMain
    }
}

public enum ScreenShareDisplays {
    /// Maps `displayId` + normalized 0–1 coordinates onto the display's
    /// `CGDisplayBounds`. A display that has been unplugged mid-session falls
    /// back to the main display rather than dropping the event, so a remote
    /// pointer never lands on a screen that is not there.
    public static func resolve(
        point: ScreenShareNormalizedPoint, displays: ScreenShareDisplaySource
    ) -> ScreenShareResolvedPoint? {
        if let bounds = displays.bounds(of: point.displayId) {
            return ScreenShareResolvedPoint(
                displayId: point.displayId,
                position: ScreenSharePolicy.map(point: point, bounds: bounds),
                fellBackToMain: false)
        }
        let main = displays.mainDisplayId()
        guard let bounds = displays.bounds(of: main) else { return nil }
        return ScreenShareResolvedPoint(
            displayId: main,
            position: ScreenSharePolicy.map(point: point, bounds: bounds),
            fellBackToMain: true)
    }
}

// MARK: - Input

public enum ScreenShareMouseButton: String, Sendable, Codable, Equatable {
    case left, right
}

/// A drag the phone performs with one finger: press, move, release. Sent as
/// three phases rather than a single gesture so a drag that the Mac interrupts
/// (a kill, a lock) can never leave a button held down.
public enum ScreenShareDragPhase: String, Sendable, Codable, Equatable {
    case begin, move, end
}

/// Modifier keys carried by a remote key event.
public struct ScreenShareModifiers: OptionSet, Sendable, Equatable {
    public let rawValue: Int
    public init(rawValue: Int) { self.rawValue = rawValue }
    public static let command = ScreenShareModifiers(rawValue: 1 << 0)
    public static let shift = ScreenShareModifiers(rawValue: 1 << 1)
    public static let option = ScreenShareModifiers(rawValue: 1 << 2)
    public static let control = ScreenShareModifiers(rawValue: 1 << 3)
}

/// One key combination from the phone: a named key plus modifiers, written
/// `cmd+shift+z` on the data channel. The key names are a closed list — a
/// remote phone cannot post a raw key code the Mac never offered.
public struct ScreenShareKeyCombo: Sendable, Equatable {
    public var code: UInt16
    public var modifiers: ScreenShareModifiers

    public init(code: UInt16, modifiers: ScreenShareModifiers) {
        self.code = code; self.modifiers = modifiers
    }

    /// Modifier names, in the order the phone writes them.
    public static let modifierNames: [(name: String, modifier: ScreenShareModifiers)] = [
        ("ctrl", .control), ("opt", .option), ("shift", .shift), ("cmd", .command),
    ]

    /// Virtual key codes (`kVK_*`) of the keys a phone may name.
    public static let keys: [String: UInt16] = {
        var keys: [String: UInt16] = [
            "return": 36, "tab": 48, "space": 49, "backspace": 51, "escape": 53, "delete": 117,
            "left": 123, "right": 124, "down": 125, "up": 126,
            "home": 115, "end": 119, "pageup": 116, "pagedown": 121,
        ]
        let letters: [(String, UInt16)] = [
            ("a", 0), ("s", 1), ("d", 2), ("f", 3), ("h", 4), ("g", 5), ("z", 6), ("x", 7), ("c", 8), ("v", 9),
            ("b", 11), ("q", 12), ("w", 13), ("e", 14), ("r", 15), ("y", 16), ("t", 17), ("o", 31), ("u", 32),
            ("i", 34), ("p", 35), ("l", 37), ("j", 38), ("k", 40), ("n", 45), ("m", 46),
        ]
        for (letter, code) in letters { keys[letter] = code }
        let digits: [UInt16] = [29, 18, 19, 20, 21, 23, 22, 26, 28, 25]
        for (digit, code) in digits.enumerated() { keys[String(digit)] = code }
        return keys
    }()

    /// Reads `cmd+c`, `return`, `ctrl+opt+left`… Each modifier at most once,
    /// then exactly one key, all lower case. Anything else is nil.
    public static func parse(_ combo: String) -> ScreenShareKeyCombo? {
        guard combo.utf8.count <= 40 else { return nil }
        let parts = combo.split(separator: "+", omittingEmptySubsequences: false).map(String.init)
        guard let keyName = parts.last, let code = keys[keyName] else { return nil }
        var modifiers: ScreenShareModifiers = []
        for name in parts.dropLast() {
            guard let modifier = modifierNames.first(where: { $0.name == name })?.modifier,
                  !modifiers.contains(modifier) else { return nil }
            modifiers.insert(modifier)
        }
        return ScreenShareKeyCombo(code: code, modifiers: modifiers)
    }
}

/// Splits committed text into the pieces `CGEventKeyboardSetUnicodeString`
/// carries: the window server drops what lies past 20 UTF-16 units in one
/// event. A grapheme (a Hangul syllable, an emoji with its modifiers) is never
/// cut in half; one longer than a whole chunk travels alone.
public enum ScreenShareTextChunks {
    public static let maximumUnits = 20

    public static func split(_ text: String, maximumUnits: Int = maximumUnits) -> [[UInt16]] {
        var chunks: [[UInt16]] = []
        var current: [UInt16] = []
        for character in text {
            let units = Array(String(character).utf16)
            if !current.isEmpty, current.count + units.count > maximumUnits {
                chunks.append(current)
                current = []
            }
            current.append(contentsOf: units)
        }
        if !current.isEmpty { chunks.append(current) }
        return chunks
    }
}

/// One remote input event. Korean arrives as committed text, never as jamo
/// keystrokes, so the host does not need an input method of its own.
public enum ScreenShareInputEvent: Sendable, Equatable {
    case move(ScreenShareNormalizedPoint)
    case click(ScreenShareNormalizedPoint, button: ScreenShareMouseButton, clickCount: Int)
    case drag(ScreenShareNormalizedPoint, phase: ScreenShareDragPhase)
    case scroll(ScreenShareNormalizedPoint, deltaX: Int32, deltaY: Int32)
    case text(String)
    case key(code: UInt16, modifiers: ScreenShareModifiers)
}

/// Where admitted input actually goes. `CGEventPost` in production.
public protocol ScreenShareInputSink: Sendable {
    func move(to position: CGPoint, displayId: UInt32) async
    func click(at position: CGPoint, displayId: UInt32, button: ScreenShareMouseButton, clickCount: Int) async
    /// One phase of a left-button drag.
    func drag(at position: CGPoint, displayId: UInt32, phase: ScreenShareDragPhase) async
    func scroll(at position: CGPoint, displayId: UInt32, deltaX: Int32, deltaY: Int32) async
    /// Committed text — the whole string at once, so Hangul arrives composed.
    func commitText(_ text: String) async
    func key(code: UInt16, modifiers: ScreenShareModifiers) async
}

/// Why the host refused an input event. Nothing here names the keys involved.
public enum ScreenShareInputRejection: Error, Sendable, Equatable {
    /// No live control session with that id — a view-only phone, or a session
    /// that has already been torn down.
    case notControlSession
    /// The lock screen, secure input, a local HID pause, or a kill that has
    /// already happened.
    case blocked
    /// The named display is gone and there is no main display to fall back to.
    case noDisplay
}

/// The Mac pasteboard, as the clipboard buttons reach it. Concealed types (a
/// password manager's) are never read: the Mac answers `concealed` instead, and
/// the phone shows that rather than a blank paste.
public protocol ScreenSharePasteboard: Sendable {
    func read() -> ScreenSharePasteboardRead
    func write(_ text: String)
}

public struct ScreenSharePasteboardRead: Sendable, Equatable {
    public var text: String?
    /// True when the pasteboard carries a type marked concealed; `text` is nil.
    public var concealed: Bool

    public init(text: String?, concealed: Bool = false) {
        self.text = text; self.concealed = concealed
    }

    public static let empty = ScreenSharePasteboardRead(text: nil)
}

// MARK: - Environment

/// The two machine states that stop frames and reject injection. Read from the
/// system in production (`IsSecureEventInputEnabled`, the console session's
/// lock flag); supplied directly by tests.
public protocol ScreenShareEnvironmentProbe: Sendable {
    func screenLocked() -> Bool
    func secureInputActive() -> Bool
}

// MARK: - Local activity

/// Tells the Mac's own remote input apart from a person at the Mac. Every
/// event the injector posts carries `marker` in `eventSourceUserData`; the
/// local-HID monitor skips those, so the phone's typing never pauses itself.
public enum ScreenShareInjectionTag {
    public static let marker: Int64 = 0x4D43_5353  // 'MCSS'

    /// True for an event a person made, which pauses remote input for 2 s.
    public static func isLocalActivity(eventSourceUserData: Int64) -> Bool {
        eventSourceUserData != marker
    }
}

// MARK: - Kill switch

/// The hotkey that stops every screen-share session. Deliberately not a
/// single-modifier chord: it must be hard to hit by accident, and it must not
/// collide with Force Quit (⌥⌘⎋).
public struct ScreenShareKillHotkey: Sendable, Equatable {
    public var keyCode: UInt16
    public var modifiers: ScreenShareModifiers

    public init(keyCode: UInt16, modifiers: ScreenShareModifiers) {
        self.keyCode = keyCode; self.modifiers = modifiers
    }

    /// ⌃⌥⌘K.
    public static let standard = ScreenShareKillHotkey(keyCode: 40, modifiers: [.control, .option, .command])

    public func matches(keyCode: UInt16, modifiers: ScreenShareModifiers) -> Bool {
        keyCode == self.keyCode && modifiers == self.modifiers
    }

    /// The chord is held only while a session is live. On an idle Mac it would
    /// take ⌃⌥⌘K away from every other app for a switch with nothing to stop.
    public static func isRegistered(for state: ScreenShareIndicatorState) -> Bool { state.isActive }
}
