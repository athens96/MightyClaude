import Testing
@testable import MightyCore

/// Status v2 (concept A, "글리프 행"): one glyph per state, shared by the sidebar rows,
/// the pane header, the tabs and the dashboard, with marks that hold 3:1 in both modes.
struct StatusGlyphTests {
    @Test func eachStateHasItsOwnGlyph() {
        #expect(StatusGlyphKind(tone: .run) == .spark)
        #expect(StatusGlyphKind(tone: .wait) == .question)
        #expect(StatusGlyphKind(tone: .done) == .check)
        #expect(StatusGlyphKind(tone: .stop) == .slashedRing)
        #expect(StatusGlyphKind(tone: .err) == .exclamation)
        #expect(StatusGlyphKind(tone: .idle) == .ring)
        let states = DesignTone.allCases.map { StatusGlyphKind(tone: $0) }
        #expect(Set(states).count == DesignTone.allCases.count)
    }

    @Test func aPaneThatIsNotAnAgentsShowsItsOwnSymbolOnlyWhileIdle() {
        #expect(StatusGlyphKind(tone: .idle, kind: SessionKind.shell) == .pane)
        #expect(StatusGlyphKind(tone: .idle, kind: SessionKind.browser) == .pane)
        #expect(StatusGlyphKind(tone: .idle, kind: SessionKind.claude) == .ring)
        #expect(StatusGlyphKind(tone: .run, kind: SessionKind.shell) == .spark)
        #expect(StatusGlyphKind(tone: .err, kind: SessionKind.shell) == .exclamation)
    }

    @Test func statusStringsReachTheirGlyphs() {
        #expect(StatusGlyphKind(tone: DesignTone(status: "waiting")) == .question)
        #expect(StatusGlyphKind(tone: DesignTone(status: "failed")) == .exclamation)
        #expect(StatusGlyphKind(tone: DesignTone(status: "cancelled")) == .slashedRing)
        #expect(StatusGlyphKind(tone: DesignTone(status: "something-new")) == .ring)
    }

    @Test func onlyTheSparkTurnsAndOnlyWaitAndErrorAreDiscs() {
        #expect(StatusGlyphKind.allCases.filter(\.turns) == [.spark])
        #expect(Set(StatusGlyphKind.allCases.filter(\.isDisc)) == [.question, .exclamation])
    }

    @Test func glyphColoursFollowTheMockup() {
        let light = DesignTokens.light, dark = DesignTokens.dark
        #expect(!light.isDark && dark.isDark)
        // By day the fills; by night the pale inks (status-v2 sv2.css --m-*).
        #expect(light.glyph(.run).hex == "#2A5FEE" && light.glyph(.done).hex == "#08804A" && light.glyph(.stop).hex == "#667085")
        #expect(dark.glyph(.run).hex == "#7FA3FF" && dark.glyph(.done).hex == "#5BD49A" && dark.glyph(.stop).hex == "#A9B1C2")
        #expect(light.glyph(.idle).hex == "#4F5869" && dark.glyph(.idle).hex == "#A9B1C2")
        // The two discs are the same amber and red in both modes, with their own inks.
        for palette in [light, dark] {
            #expect(palette.discFill(.wait).hex == "#FFA81F" && palette.discInk(.wait).hex == "#2B1B00")
            #expect(palette.discFill(.err).hex == "#D42F22" && palette.discInk(.err).hex == "#FFFFFF")
        }
    }

    @Test(arguments: ["light", "dark"])
    func lineGlyphsClearThreeToOneOnEveryGround(_ mode: String) {
        let palette = DesignTokens.palette(theme: mode)
        let grounds: [(String, DesignColor)] = [
            ("page", palette.page), ("card", palette.card), ("cardRaised", palette.cardRaised), ("sidebar", palette.sidebar),
        ]
        for tone in [DesignTone.run, .done, .stop, .idle] {
            for (name, ground) in grounds {
                let ratio = palette.glyph(tone).contrast(with: ground)
                #expect(ratio >= 3, "\(mode): glyph(\(tone)) on \(name) is \(ratio)")
            }
        }
    }

    @Test(arguments: ["light", "dark"])
    func discMarksClearTheirDiscs(_ mode: String) {
        let palette = DesignTokens.palette(theme: mode)
        for tone in [DesignTone.wait, .err] {
            let ratio = palette.discInk(tone).contrast(with: palette.discFill(tone))
            #expect(ratio >= 4.5, "\(mode): disc ink on \(tone) is \(ratio)")
        }
    }
}
