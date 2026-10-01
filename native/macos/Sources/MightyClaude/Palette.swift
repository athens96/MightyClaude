import SwiftUI
import AppKit
import MightyCore

// Keep using the macOS 14 property wrapper when an SDK also exports a State macro.
// Command Line Tools ship SwiftUI itself but may not include its macro plugin.
typealias ViewState<Value> = SwiftUI.State<Value>

/// Concept D ("카드 대시보드") colours, read from `DesignTokens` in MightyCore where
/// DesignTokenContrastTests holds them to WCAG AA. Each colour is a dynamic NSColor
/// that picks the light or dark token from the appearance it is drawn in; the window's
/// appearance follows `.preferredColorScheme`, which follows `snapshot.theme`, so the
/// app's own theme toggle switches every colour, not only the system setting.
///
/// Words and icons on a surface use the inks (`accent`, `waitText`, `doneText`,
/// `errText`, `stopText`, the block inks); the fills (`run`…`stop`) are for solid
/// shapes that carry `onStatus`/`onWait`, and `mark(_:)` is for dots and nodes.
enum Palette {
    // Surfaces.
    /// The D page under the panes and the detail column.
    static let canvas = token(\.page)
    /// The D card: panes, graph blocks, popovers drawn by the app.
    static let panel = token(\.card)
    static let raised = token(\.cardRaised)
    /// Solid sidebar, never vibrancy: the wallpaper must not decide the contrast.
    static let sidebar = token(\.sidebar)
    static let border = token(\.line)
    static let track = token(\.track)
    /// A neutral wash for hover and selected rows, on any surface.
    static let subtle = Color.primary.opacity(0.035)

    // Inks.
    /// Primary words, and the fill of the ink button (with `panel` on it).
    static let ink = token(\.ink)
    /// Secondary words on page, card and raised strip — never on the sidebar itself,
    /// and in place of the system `.secondary`, which falls short of AA on D surfaces.
    static let ink2 = token(\.ink2)
    /// The run blue as a word, link, selection or control tint.
    static let accent = token(\.accent)
    static let accentSoft = token(\.accentSoft)
    static let onAccent = token(\.onAccent)
    static let sidebarAccent = token(\.sidebarAccent)
    static let sidebarInk2 = token(\.sidebarInk2)
    static let waitText = token(\.waitText)
    static let doneText = token(\.doneText)
    static let errText = token(\.errText)
    static let stopText = token(\.stopText)

    // Status fills and their soft tints.
    static let run = token(\.run)
    static let wait = token(\.wait)
    static let onWait = token(\.onWait)
    static let done = token(\.done)
    static let err = token(\.err)
    static let stop = token(\.stop)
    static let idle = token(\.idle)
    static let onStatus = token(\.onStatus)
    static let runSoft = token(\.runSoft)
    static let waitSoft = token(\.waitSoft)
    static let doneSoft = token(\.doneSoft)
    static let errSoft = token(\.errSoft)
    static let stopSoft = token(\.stopSoft)

    // Block kinds, as inks (AA as words, 3:1 as icons).
    static let agentText = token(\.agentText)
    static let taskText = token(\.taskText)
    static let steerText = token(\.steerText)
    static let compactText = token(\.compactText)
    static let questionText = token(\.questionText)

    // AppKit copies for attributed strings.
    static let nsAccent = nsToken(\.accent)
    static let nsErrText = nsToken(\.errText)

    /// The ink for a status word or icon (`running`, `waiting`, `completed`, `error`, `stopped`…).
    static func text(_ status: String) -> Color { text(DesignTone(status: status)) }

    /// The ink for a tone's words and icons on page, card or raised strip.
    static func text(_ tone: DesignTone) -> Color {
        switch tone {
        case .run: accent
        case .wait: waitText
        case .done: doneText
        case .err: errText
        case .stop: stopText
        case .idle: ink2
        }
    }

    /// The colour of a status dot or node: 3:1 on page, card and sidebar.
    static func mark(_ status: String) -> Color { mark(DesignTone(status: status)) }

    /// The colour of a tone's dot or node: 3:1 on page, card and sidebar.
    static func mark(_ tone: DesignTone) -> Color {
        switch tone {
        case .run: run
        case .wait: waitText
        case .done: done
        case .err: err
        case .stop: stop
        case .idle: idleMark
        }
    }

    private static let idleMark = token(\.ink3)

    static func token(_ path: KeyPath<DesignPalette, DesignColor>) -> Color { Color(nsColor: nsToken(path)) }

    /// `alpha` is applied per appearance here, since a provider colour is not
    /// guaranteed to stay dynamic through `withAlphaComponent`.
    static func nsToken(_ path: KeyPath<DesignPalette, DesignColor>, alpha: CGFloat = 1) -> NSColor {
        let light = nsColor(DesignTokens.light[keyPath: path]).withAlphaComponent(alpha)
        let dark = nsColor(DesignTokens.dark[keyPath: path]).withAlphaComponent(alpha)
        return NSColor(name: nil) { appearance in
            appearance.bestMatch(from: [.aqua, .darkAqua]) == .darkAqua ? dark : light
        }
    }

    private static func nsColor(_ color: DesignColor) -> NSColor {
        NSColor(srgbRed: CGFloat(color.red) / 255, green: CGFloat(color.green) / 255, blue: CGFloat(color.blue) / 255, alpha: 1)
    }

    static func symbol(_ provider: String) -> String {
        switch provider { case "codex": return "hexagon"; case "gemini": return "sparkle"; default: return "asterisk" }
    }

    static func name(_ provider: String) -> String {
        switch provider { case "codex": return "Codex"; case "gemini": return "Gemini"; default: return "Claude" }
    }

    /// A pane's status in words, through the locale (`session.state.*`).
    static func status(_ value: String) -> String {
        switch value {
        case "running": L("session.state.running")
        case "completed": L("session.state.completed")
        case "error": L("session.state.error")
        case "stopped": L("session.state.stopped")
        default: L("session.state.idle")
        }
    }
}

struct StatusDot: View {
    let status: String
    var body: some View {
        Circle().fill(Palette.mark(status)).frame(width: 5, height: 5)
    }
}
