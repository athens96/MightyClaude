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

/// Modifier keys carried by a remote key event.
public struct ScreenShareModifiers: OptionSet, Sendable, Equatable {
    public let rawValue: Int
    public init(rawValue: Int) { self.rawValue = rawValue }
    public static let command = ScreenShareModifiers(rawValue: 1 << 0)
    public static let shift = ScreenShareModifiers(rawValue: 1 << 1)
    public static let option = ScreenShareModifiers(rawValue: 1 << 2)
    public static let control = ScreenShareModifiers(rawValue: 1 << 3)
}

/// One remote input event. Korean arrives as committed text, never as jamo
/// keystrokes, so the host does not need an input method of its own.
public enum ScreenShareInputEvent: Sendable, Equatable {
    case move(ScreenShareNormalizedPoint)
    case click(ScreenShareNormalizedPoint, button: ScreenShareMouseButton, clickCount: Int)
    case scroll(ScreenShareNormalizedPoint, deltaX: Int32, deltaY: Int32)
    case text(String)
    case key(code: UInt16, modifiers: ScreenShareModifiers)
}

/// Where admitted input actually goes. `CGEventPost` in production.
public protocol ScreenShareInputSink: Sendable {
    func move(to position: CGPoint, displayId: UInt32) async
    func click(at position: CGPoint, displayId: UInt32, button: ScreenShareMouseButton, clickCount: Int) async
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

// MARK: - Environment

/// The two machine states that stop frames and reject injection. Read from the
/// system in production (`IsSecureEventInputEnabled`, the console session's
/// lock flag); supplied directly by tests.
public protocol ScreenShareEnvironmentProbe: Sendable {
    func screenLocked() -> Bool
    func secureInputActive() -> Bool
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
}
