import Foundation

/// The small mark in front of a pane's title (status v2, concept A "글리프 행"): one
/// glyph per state, the same in the sidebar, the pane header and the dashboard.
/// Only the amber "?" and the red "!" are filled discs; the rest are line marks, so
/// the colour that is left on screen is what wants a look.
public enum StatusGlyphKind: String, CaseIterable, Sendable {
    /// Running: an eight-armed spark that turns slowly (still under Reduce Motion).
    case spark
    /// Waiting on the user: the amber disc with a "?".
    case question
    /// Finished: a check.
    case check
    /// Stopped by the user: a ring with a slash.
    case slashedRing
    /// Stopped by an error: the red disc with a "!".
    case exclamation
    /// An idle agent pane: a small ring.
    case ring
    /// An idle pane that is not an agent's (a shell, a browser, the files pane):
    /// the pane's own symbol.
    case pane

    /// The glyph for a pane's tone. A pane that is not an agent's shows its own
    /// symbol while it is idle; any other state shows the state's glyph.
    public init(tone: DesignTone, kind: String = SessionKind.claude) {
        switch tone {
        case .run: self = .spark
        case .wait: self = .question
        case .done: self = .check
        case .stop: self = .slashedRing
        case .err: self = .exclamation
        case .idle: self = kind == SessionKind.claude ? .ring : .pane
        }
    }

    /// Only the running spark moves.
    public var turns: Bool { self == .spark }

    /// A filled disc carrying its own ink, rather than a line mark.
    public var isDisc: Bool { self == .question || self == .exclamation }
}

extension DesignPalette {
    /// The colour of a status glyph's lines on page, card, raised strip or sidebar
    /// (3:1, held by DesignTokenContrastTests). By day a line mark takes the tone's
    /// fill; by night the fills sink into the navy, so it takes the tone's pale ink.
    /// An idle mark is the sidebar's quiet ink. The two discs are `discFill`.
    public func glyph(_ tone: DesignTone) -> DesignColor {
        switch tone {
        case .run, .done, .stop: isDark ? text(tone) : fill(tone)
        case .wait, .err: discFill(tone)
        case .idle: sidebarInk2
        }
    }

    /// The disc behind the "?" (amber) and the "!" (red); the same in both modes.
    public func discFill(_ tone: DesignTone) -> DesignColor {
        tone == .wait ? wait : err
    }

    /// The "?" or "!" drawn on its disc: the amber takes its own dark ink, the red white.
    public func discInk(_ tone: DesignTone) -> DesignColor {
        tone == .wait ? onWait : onStatus
    }
}
