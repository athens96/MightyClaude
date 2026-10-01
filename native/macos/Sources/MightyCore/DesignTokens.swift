import Foundation

/// One opaque sRGB colour of the design palette, kept as plain data so the
/// contrast rules can be checked without AppKit.
public struct DesignColor: Equatable, Hashable, Sendable {
    public let red: UInt8
    public let green: UInt8
    public let blue: UInt8

    public init(_ hex: UInt32) {
        red = UInt8((hex >> 16) & 0xFF)
        green = UInt8((hex >> 8) & 0xFF)
        blue = UInt8(hex & 0xFF)
    }

    public var hex: String { String(format: "#%02X%02X%02X", red, green, blue) }

    /// WCAG 2.x relative luminance.
    public var relativeLuminance: Double {
        func channel(_ value: UInt8) -> Double {
            let c = Double(value) / 255
            return c <= 0.03928 ? c / 12.92 : pow((c + 0.055) / 1.055, 2.4)
        }
        return 0.2126 * channel(red) + 0.7152 * channel(green) + 0.0722 * channel(blue)
    }

    /// WCAG 2.x contrast ratio, 1...21, whichever colour is lighter.
    public func contrast(with other: DesignColor) -> Double {
        let a = relativeLuminance, b = other.relativeLuminance
        return (max(a, b) + 0.05) / (min(a, b) + 0.05)
    }
}

/// The six colours concept D gives to state. Every status drawn by the app goes
/// through `init(status:)`, so a word the host adds later lands on `idle`
/// instead of a colour it did not earn (mirrors the phone's `toneOf`).
public enum DesignTone: String, CaseIterable, Sendable {
    case run, wait, done, err, stop, idle

    public init(status: String) {
        switch status {
        case "running": self = .run
        case "waiting": self = .wait
        case "completed": self = .done
        case "error", "failed": self = .err
        case "stopped", "cancelled", "interrupted": self = .stop
        default: self = .idle
        }
    }
}

/// "카드 대시보드" (concept D) for one appearance. The values are the phone's
/// AA-checked palette (`mobile/src/theme/index.ts`) plus the Mac's solid sidebar
/// and its two sidebar inks from the Mac mockup (`mac-D.css`).
///
/// Three kinds of colour, and where each may go (held by DesignTokenContrastTests):
/// - surfaces: `page`, `card`, `cardRaised`, `sidebar`, the soft tints, `codeSurface`;
/// - inks (words and icons on surfaces): `ink*`, `accent`, the `*Text` status inks,
///   the block inks, `sidebarInk2`/`sidebarAccent` — 4.5:1 on what they sit on;
/// - fills (`run`…`stop`, `idle`, `agent`, `task`): solid shapes carrying `onStatus`,
///   `onWait` or `onTask`, and dots/marks at 3:1 on page, card and sidebar.
///   `wait` is too light to be a mark on a light surface; marks use `waitText` there.
public struct DesignPalette: Equatable, Sendable {
    // Surfaces.
    public let page: DesignColor
    public let card: DesignColor
    /// A step inside a card: status strip, pressed rows, inline code.
    public let cardRaised: DesignColor
    /// Solid, never vibrancy: the wallpaper must not decide the contrast.
    public let sidebar: DesignColor
    public let line: DesignColor
    /// Progress rails and segmented-control tracks.
    public let track: DesignColor

    // Inks.
    public let ink: DesignColor
    public let ink2: DesignColor
    public let ink3: DesignColor
    /// The run blue where it is a word, a link, a selection or a control tint.
    public let accent: DesignColor
    public let accentSoft: DesignColor
    /// Text on a filled accent (the primary button).
    public let onAccent: DesignColor
    /// The sidebar's secondary words and its accent: darker by day than `ink2`/`accent`.
    public let sidebarInk2: DesignColor
    public let sidebarAccent: DesignColor

    // Status fills, their soft tints and their text-safe inks.
    public let run: DesignColor
    public let runSoft: DesignColor
    public let wait: DesignColor
    public let waitSoft: DesignColor
    public let onWait: DesignColor
    public let waitText: DesignColor
    public let done: DesignColor
    public let doneSoft: DesignColor
    public let doneText: DesignColor
    public let err: DesignColor
    public let errSoft: DesignColor
    public let errText: DesignColor
    public let stop: DesignColor
    public let stopSoft: DesignColor
    public let stopText: DesignColor
    /// The fill behind something simply idle.
    public let idle: DesignColor
    /// White on run, done, err, stop, idle and (as a glyph) agent.
    public let onStatus: DesignColor

    // Block kinds. `agent`/`task` are fills; the inks are for words and icons.
    public let agent: DesignColor
    public let agentText: DesignColor
    public let task: DesignColor
    public let taskText: DesignColor
    public let onTask: DesignColor
    public let steerText: DesignColor
    public let compactText: DesignColor
    public let questionText: DesignColor

    // Fenced code: an ink block in both modes.
    public let codeSurface: DesignColor
    public let codeText: DesignColor

    /// The fill for a tone.
    public func fill(_ tone: DesignTone) -> DesignColor {
        switch tone {
        case .run: run
        case .wait: wait
        case .done: done
        case .err: err
        case .stop: stop
        case .idle: idle
        }
    }

    /// The text-safe ink for a tone: words and icons on page, card or raised strip.
    public func text(_ tone: DesignTone) -> DesignColor {
        switch tone {
        case .run: accent
        case .wait: waitText
        case .done: doneText
        case .err: errText
        case .stop: stopText
        case .idle: ink2
        }
    }

    /// The soft tint behind a tone's ink.
    public func soft(_ tone: DesignTone) -> DesignColor {
        switch tone {
        case .run: runSoft
        case .wait: waitSoft
        case .done: doneSoft
        case .err: errSoft
        case .stop, .idle: stopSoft
        }
    }

    /// A small mark (dot, node) for a tone on page, card or sidebar: the fill
    /// where it holds 3:1, the ink for amber, and the quiet ink for idle.
    public func mark(_ tone: DesignTone) -> DesignColor {
        switch tone {
        case .wait: waitText
        case .idle: ink3
        default: fill(tone)
        }
    }
}

public enum DesignTokens {
    public static let light = DesignPalette(
        page: DesignColor(0xECEEF3), card: DesignColor(0xFFFFFF), cardRaised: DesignColor(0xF5F7FB),
        sidebar: DesignColor(0xE2E6ED), line: DesignColor(0xDEE2EA), track: DesignColor(0xDDE1E9),
        ink: DesignColor(0x0E1320), ink2: DesignColor(0x5A6377), ink3: DesignColor(0x616A7C),
        accent: DesignColor(0x2459E6), accentSoft: DesignColor(0xE6EDFF), onAccent: DesignColor(0xFFFFFF),
        sidebarInk2: DesignColor(0x4F5869), sidebarAccent: DesignColor(0x1F4FD1),
        run: DesignColor(0x2A5FEE), runSoft: DesignColor(0xE6EDFF),
        wait: DesignColor(0xFFA81F), waitSoft: DesignColor(0xFFF3DE), onWait: DesignColor(0x2B1B00), waitText: DesignColor(0x8A5300),
        done: DesignColor(0x08804A), doneSoft: DesignColor(0xE2F6EA), doneText: DesignColor(0x06703F),
        err: DesignColor(0xD42F22), errSoft: DesignColor(0xFDE6E4), errText: DesignColor(0xB42318),
        stop: DesignColor(0x667085), stopSoft: DesignColor(0xEEF0F4), stopText: DesignColor(0x4F5869),
        idle: DesignColor(0x0E1320), onStatus: DesignColor(0xFFFFFF),
        agent: DesignColor(0x8A5CF6), agentText: DesignColor(0x6D3FD9),
        task: DesignColor(0x0EA5B7), taskText: DesignColor(0x0A7480), onTask: DesignColor(0x0E1320),
        steerText: DesignColor(0xA33D8F), compactText: DesignColor(0x4B5BB8), questionText: DesignColor(0x8A5300),
        codeSurface: DesignColor(0x0E1320), codeText: DesignColor(0xD8DEEA)
    )

    /// The night side: ink navy page (never black), slate cards; the inks lift to
    /// pale tints, the fills stay the same saturated set as by day.
    public static let dark = DesignPalette(
        page: DesignColor(0x0B0F19), card: DesignColor(0x151B29), cardRaised: DesignColor(0x1D2435),
        sidebar: DesignColor(0x0F1420), line: DesignColor(0x283043), track: DesignColor(0x1D2435),
        ink: DesignColor(0xEEF1F7), ink2: DesignColor(0xA9B1C2), ink3: DesignColor(0x8E97AA),
        accent: DesignColor(0x7FA3FF), accentSoft: DesignColor(0x1A2750), onAccent: DesignColor(0x0B0F19),
        sidebarInk2: DesignColor(0xA9B1C2), sidebarAccent: DesignColor(0x7FA3FF),
        run: DesignColor(0x2A5FEE), runSoft: DesignColor(0x1A2750),
        wait: DesignColor(0xFFA81F), waitSoft: DesignColor(0x3A2A0D), onWait: DesignColor(0x2B1B00), waitText: DesignColor(0xFFC45C),
        done: DesignColor(0x08804A), doneSoft: DesignColor(0x0F2E22), doneText: DesignColor(0x5BD49A),
        err: DesignColor(0xD42F22), errSoft: DesignColor(0x3A1A18), errText: DesignColor(0xFF8A80),
        stop: DesignColor(0x667085), stopSoft: DesignColor(0x222939), stopText: DesignColor(0xA9B1C2),
        idle: DesignColor(0x2A3347), onStatus: DesignColor(0xFFFFFF),
        agent: DesignColor(0x8A5CF6), agentText: DesignColor(0xB9A0FF),
        task: DesignColor(0x0EA5B7), taskText: DesignColor(0x5ED3E0), onTask: DesignColor(0x0E1320),
        steerText: DesignColor(0xE58FD0), compactText: DesignColor(0x9AA6F5), questionText: DesignColor(0xFFC45C),
        codeSurface: DesignColor(0x05070D), codeText: DesignColor(0xD8DEEA)
    )

    /// The app's own theme setting (`snapshot.theme`): only "light" is light,
    /// matching the `.preferredColorScheme` mapping.
    public static func palette(theme: String) -> DesignPalette {
        theme == "light" ? light : dark
    }
}
