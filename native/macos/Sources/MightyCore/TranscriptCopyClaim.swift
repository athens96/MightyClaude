import Foundation

/// Who ⌘C belongs to when a drawn selection and the keyboard focus have drifted
/// apart. AppKit routes the Copy key equivalent to the first responder alone,
/// while a selection stays painted in the view that made it — so a Mighty graph
/// card that lost focus to a pan, a resize or a click on its own chrome keeps
/// showing a selection it can no longer copy. The card has to ask for the key
/// back, but only against an allowlist: anything focused that implements Copy
/// itself keeps its own ⌘C, empty selection included. A composer with nothing
/// but a caret must stay the no-op macOS users expect, not a silent copy of
/// text somewhere else on the canvas.
public enum TranscriptCopyClaim {
    /// `NSEvent.ModifierFlags.command.rawValue` and `.deviceIndependentFlagsMask`,
    /// spelled out so this rule needs no AppKit and stays unit-testable.
    static let commandFlag: UInt = 1 << 20
    static let deviceIndependentFlags: UInt = 0xFFFF_0000
    /// Caps Lock, Fn and the numeric-pad bit ride along with whatever else the
    /// keyboard happens to be doing and say nothing about which shortcut was
    /// meant. They are cleared before the comparison rather than allowed to
    /// defeat it: `.capsLock`, `.function`, `.numericPad`.
    static let incidentalFlags: UInt = (1 << 16) | (1 << 21) | (1 << 23)
    /// The key code AppKit reports for the physical C key. A Korean input
    /// source delivers "ㅊ" as the characters, so on that layout the code is
    /// the only thing left that still says "the user pressed ⌘C".
    public static let copyKeyCode: UInt16 = 8

    /// Command and nothing else, on the C key by character or by position.
    public static func isCopyKeyEquivalent(modifiers: UInt, characters: String?, keyCode: UInt16) -> Bool {
        let meaningful = modifiers & deviceIndependentFlags & ~incidentalFlags
        guard meaningful == commandFlag else { return false }
        if keyCode == copyKeyCode { return true }
        return characters?.lowercased() == "c"
    }

    /// The window's current first responder, as far as this decision cares.
    public struct Responder: Equatable, Sendable {
        /// Whether the focused thing answers Copy on its own — every NSTextView,
        /// the composer, a field editor, the Ghostty terminal view. AppKit's
        /// ordinary route to it is the right one, and claiming would shadow it.
        public var canCopyItself: Bool
        public init(canCopyItself: Bool = false) { self.canCopyItself = canCopyItself }
        /// Focus on something that cannot copy at all, such as the graph canvas.
        public static let plain = Responder()
    }

    /// Whether the asking view should copy its own selection. A view that
    /// already is the first responder never asks: AppKit's ordinary path is
    /// the right one, and claiming would only shadow it.
    public static func claims(copyKeyEquivalent: Bool, selectionLength: Int, isMostRecentlySelected: Bool,
                              isVisible: Bool, isSelectionOnScreen: Bool, isBlocked: Bool,
                              responder: Responder) -> Bool {
        guard copyKeyEquivalent, selectionLength > 0, isMostRecentlySelected else { return false }
        guard isVisible, isSelectionOnScreen, !isBlocked else { return false }
        return !responder.canCopyItself
    }
}
