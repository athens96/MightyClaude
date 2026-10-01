import Testing
@testable import MightyCore

/// Concept D's palette held to WCAG 2.1 in both modes, mirroring the phone's
/// `palette-contrast.test.ts`: every word to 4.5:1 on what it sits on, every
/// status fill that carries text to 4.5:1 against that text, and every mark or
/// glyph-only fill to 3:1 (1.4.11).
struct DesignTokenContrastTests {
    typealias Token = KeyPath<DesignPalette, DesignColor>

    /// The app's two theme settings; each test reads its palette the way the app does.
    static let modes = ["light", "dark"]

    /// Body text: 4.5:1 (1.4.3).
    static let aa = 4.5
    /// Non-text marks: 3:1 (1.4.11).
    static let mark = 3.0

    /// Every text colour on the ground the app draws it on.
    static let textPairs: [(String, Token, Token)] = {
        var pairs: [(String, Token, Token)] = []
        let grounds: [(String, Token)] = [("page", \.page), ("card", \.card), ("cardRaised", \.cardRaised)]
        // Ink, its greys, the accent, the status inks and the block inks on page, card and raised strip.
        let inks: [(String, Token)] = [
            ("ink", \.ink), ("ink2", \.ink2), ("ink3", \.ink3), ("accent", \.accent),
            ("waitText", \.waitText), ("doneText", \.doneText), ("errText", \.errText), ("stopText", \.stopText),
            ("agentText", \.agentText), ("taskText", \.taskText), ("steerText", \.steerText),
            ("compactText", \.compactText), ("questionText", \.questionText),
        ]
        for (inkName, ink) in inks { for (groundName, ground) in grounds { pairs.append(("\(inkName) on \(groundName)", ink, ground)) } }
        // The solid sidebar: ink, its own secondary ink and accent, and the status words on rows.
        let sidebarInks: [(String, Token)] = [
            ("ink", \.ink), ("sidebarInk2", \.sidebarInk2), ("sidebarAccent", \.sidebarAccent),
            ("waitText", \.waitText), ("doneText", \.doneText), ("errText", \.errText), ("stopText", \.stopText),
        ]
        for (inkName, ink) in sidebarInks { pairs.append(("\(inkName) on sidebar", ink, \.sidebar)) }
        // Status inks on their own soft tints (soft pills, banners), and the selected wash.
        pairs += [
            ("accent on runSoft", \.accent, \.runSoft), ("accent on accentSoft", \.accent, \.accentSoft),
            ("ink on accentSoft", \.ink, \.accentSoft),
            ("waitText on waitSoft", \.waitText, \.waitSoft), ("doneText on doneSoft", \.doneText, \.doneSoft),
            ("errText on errSoft", \.errText, \.errSoft), ("stopText on stopSoft", \.stopText, \.stopSoft),
            ("ink2 on stopSoft", \.ink2, \.stopSoft),
            ("codeText on codeSurface", \.codeText, \.codeSurface),
            // The user's ink bubble in the transcript: its words and time, and its links,
            // which keep the bubble's words colour (underlined) since the accent fails there.
            ("bubble text (card) on ink", \.card, \.ink),
            ("bubble link (card) on ink", \.card, \.ink),
        ]
        return pairs
    }()

    /// Status fills that carry words, with the colour of those words.
    static let filledTextPairs: [(String, Token, Token)] = [
        ("onStatus on run", \.onStatus, \.run), ("onStatus on done", \.onStatus, \.done),
        ("onStatus on err", \.onStatus, \.err), ("onStatus on stop", \.onStatus, \.stop),
        ("onStatus on idle", \.onStatus, \.idle), ("onWait on wait", \.onWait, \.wait),
        ("onAccent on accent", \.onAccent, \.accent), ("onTask on task", \.onTask, \.task),
    ]

    /// Marks, dots and glyph-only fills on the surfaces they sit on.
    static let markPairs: [(String, Token, Token)] = {
        var pairs: [(String, Token, Token)] = []
        let grounds: [(String, Token)] = [("page", \.page), ("card", \.card), ("sidebar", \.sidebar)]
        for tone in DesignTone.allCases {
            for (groundName, ground) in grounds {
                pairs.append(("mark(\(tone)) on \(groundName)", \DesignPalette.[mark: tone], ground))
            }
        }
        pairs += [
            ("agent on page", \.agent, \.page), ("agent on card", \.agent, \.card),
            ("onStatus glyph on agent", \.onStatus, \.agent),
            ("accent mark on accentSoft", \.accent, \.accentSoft),
        ]
        return pairs
    }()

    @Test(arguments: modes)
    func everyWordClearsAA(_ mode: String) {
        let palette = DesignTokens.palette(theme: mode)
        for (name, foreground, background) in Self.textPairs {
            let ratio = palette[keyPath: foreground].contrast(with: palette[keyPath: background])
            #expect(ratio >= Self.aa, "\(mode): \(name) is \(ratio)")
        }
    }

    @Test(arguments: modes)
    func statusFillsCarryTheirTextAtAA(_ mode: String) {
        let palette = DesignTokens.palette(theme: mode)
        for (name, foreground, background) in Self.filledTextPairs {
            let ratio = palette[keyPath: foreground].contrast(with: palette[keyPath: background])
            #expect(ratio >= Self.aa, "\(mode): \(name) is \(ratio)")
        }
    }

    @Test(arguments: modes)
    func marksAndGlyphFillsClearThreeToOne(_ mode: String) {
        let palette = DesignTokens.palette(theme: mode)
        for (name, foreground, background) in Self.markPairs {
            let ratio = palette[keyPath: foreground].contrast(with: palette[keyPath: background])
            #expect(ratio >= Self.mark, "\(mode): \(name) is \(ratio)")
        }
    }

    @Test(arguments: modes)
    func toneInksAreTheTestedInks(_ mode: String) {
        let palette = DesignTokens.palette(theme: mode)
        // Words for a tone go through `text(_:)`; each lands on an ink checked above.
        let checked = Set(Self.textPairs.map { palette[keyPath: $0.1] })
        for tone in DesignTone.allCases { #expect(checked.contains(palette.text(tone)), "\(mode): \(tone)") }
    }

    @Test(arguments: modes)
    func cardsLiftOffThePageWithoutBecomingPanels(_ mode: String) {
        let palette = DesignTokens.palette(theme: mode)
        let lift = palette.card.contrast(with: palette.page)
        #expect(lift > 1.05 && lift < 1.5, "\(mode): \(lift)")
        #expect(palette.line.contrast(with: palette.page) > 1.05)
        #expect(palette.sidebar != palette.page)
    }

    @Test func darkPageIsInkNavyRatherThanBlack() {
        let page = DesignTokens.dark.page
        #expect(page.relativeLuminance > 0.004)
        #expect(page.blue > page.red)
    }

    @Test func statusFillsAreOneSetAcrossModes() {
        for tone in DesignTone.allCases where tone != .idle {
            #expect(DesignTokens.light.fill(tone) == DesignTokens.dark.fill(tone))
        }
        #expect(DesignTokens.light.agent == DesignTokens.dark.agent && DesignTokens.light.task == DesignTokens.dark.task)
    }

    @Test func valuesMatchThePhoneAndTheMockup() {
        let light = DesignTokens.light, dark = DesignTokens.dark
        #expect(light.run.hex == "#2A5FEE" && light.accent.hex == "#2459E6")
        #expect(light.err.hex == "#D42F22" && light.stop.hex == "#667085")
        #expect(light.wait.hex == "#FFA81F" && light.onWait.hex == "#2B1B00")
        #expect(dark.page.hex == "#0B0F19" && dark.card.hex == "#151B29")
        #expect(light.agent.hex == "#8A5CF6" && light.task.hex == "#0EA5B7")
    }

    @Test func themeAndStatusMapping() {
        #expect(DesignTokens.palette(theme: "light") == DesignTokens.light)
        #expect(DesignTokens.palette(theme: "dark") == DesignTokens.dark)
        #expect(DesignTokens.palette(theme: "") == DesignTokens.dark)
        #expect(DesignTone(status: "running") == .run && DesignTone(status: "waiting") == .wait)
        #expect(DesignTone(status: "completed") == .done && DesignTone(status: "error") == .err)
        #expect(DesignTone(status: "stopped") == .stop && DesignTone(status: "idle") == .idle)
        #expect(DesignTone(status: "something-new") == .idle)
    }

    @Test func luminanceMatchesWCAG() {
        #expect(abs(DesignColor(0xFFFFFF).contrast(with: DesignColor(0x000000)) - 21) < 1e-9)
        #expect(abs(DesignColor(0x777777).contrast(with: DesignColor(0x777777)) - 1) < 1e-9)
        #expect(DesignColor(0x0E1320).hex == "#0E1320")
    }
}

private extension DesignPalette {
    subscript(mark tone: DesignTone) -> DesignColor { mark(tone) }
}
