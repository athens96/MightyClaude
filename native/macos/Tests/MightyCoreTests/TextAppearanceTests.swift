import Foundation
import Testing
@testable import MightyCore

struct TextAppearanceTests {
    @Test func theStandardSizeDrawsEveryFontAsBefore() {
        let standard = TextAppearance.standard
        #expect(standard.scale == 1 && standard.family == nil && standard.isStandard)
        #expect(standard.transcriptFamily == nil && standard.codeFamily == nil && standard.resolvedTranscriptFamily == nil)
        for base in [4, 9, 10, 10.5, 11, 11.5, 12, 12.5, 13, 22] as [Double] { #expect(standard.size(base) == base) }
        #expect(standard.terminalSize == 12)
        #expect(standard.percent == 100)
    }

    @Test func sizesScaleToTheHalfPoint() {
        let larger = TextAppearance(scale: 1.2)
        #expect(larger.size(13) == 15.5)
        #expect(larger.size(10) == 12)
        #expect(larger.terminalSize == 14.5)
        #expect(TextAppearance(scale: 0.85).size(11) == 9.5)
        #expect(TextAppearance(scale: 1.5).size(13) == 19.5)
    }

    @Test func aScaleSnapsToTheNearestStep() {
        #expect(TextAppearance(scale: 1.16).scale == 1.2)
        #expect(TextAppearance(scale: 3).scale == 1.5)
        #expect(TextAppearance(scale: 0.2).scale == 0.85)
        for broken in [0, -1, .nan, .infinity] as [Double] { #expect(TextAppearance(scale: broken).scale == 1) }
    }

    @Test func steppingStopsAtTheEndsAndKeepsTheFamily() {
        var text = TextAppearance(family: "Helvetica Neue")
        #expect(text.canGrow && text.canShrink)
        text = text.stepped(1)
        #expect(text.scale == 1.1 && text.family == "Helvetica Neue")
        #expect(text.stepped(100).scale == 1.5 && !text.stepped(100).canGrow)
        #expect(text.stepped(-100).scale == 0.85 && !text.stepped(-100).canShrink)
        #expect(TextAppearance.standard.stepped(-1).scale == 0.9)
    }

    @Test func everyFontMustBeStandardForTheStandardSetting() {
        #expect(!TextAppearance(family: "Avenir Next").isStandard)
        #expect(!TextAppearance(transcriptFamily: "Avenir Next").isStandard)
        #expect(!TextAppearance(codeFamily: "Menlo").isStandard)
        #expect(TextAppearance(transcriptFamily: " ", codeFamily: "\n").isStandard)
    }

    @Test func theConversationFollowsTheInterfaceUntilItHasItsOwnFont() {
        #expect(TextAppearance(family: "Avenir Next").resolvedTranscriptFamily == "Avenir Next")
        #expect(TextAppearance(family: "Avenir Next", transcriptFamily: " Georgia ").resolvedTranscriptFamily == "Georgia")
        #expect(TextAppearance(transcriptFamily: "Georgia").resolvedTranscriptFamily == "Georgia")
    }

    @Test func changingOneSettingKeepsTheOthers() {
        let text = TextAppearance(scale: 1.2, family: "Avenir Next", transcriptFamily: "Georgia", codeFamily: "Menlo")
        #expect(text.stepped(1) == TextAppearance(scale: 1.3, family: "Avenir Next", transcriptFamily: "Georgia", codeFamily: "Menlo"))
        #expect(text.with(scale: 1) == TextAppearance(family: "Avenir Next", transcriptFamily: "Georgia", codeFamily: "Menlo"))
        #expect(text.with(family: nil) == TextAppearance(scale: 1.2, transcriptFamily: "Georgia", codeFamily: "Menlo"))
        #expect(text.with(transcriptFamily: "") == TextAppearance(scale: 1.2, family: "Avenir Next", codeFamily: "Menlo"))
        #expect(text.with(codeFamily: " JetBrains Mono ") == TextAppearance(scale: 1.2, family: "Avenir Next", transcriptFamily: "Georgia", codeFamily: "JetBrains Mono"))
    }

    @Test func theCodePickerOffersFixedPitchAndKnownMonospacedFamilies() {
        let fonts: [(family: String, fixedPitch: Bool)] = [
            ("Helvetica", false), ("Menlo", true), ("Iosevka", true), ("D2Coding", false), ("SF Mono", false),
            ("JetBrains Mono", false), (".SF NS Mono", true), ("Menlo", true), ("Georgia", false),
        ]
        #expect(TextAppearance.codeFamilies(fonts) == ["Menlo", "Iosevka", "D2Coding", "SF Mono", "JetBrains Mono"])
        #expect(TextAppearance.codeFamilies([]).isEmpty)
    }

    @Test func aBlankFamilyIsTheSystemFont() {
        #expect(TextAppearance(family: "  ").family == nil)
        #expect(TextAppearance(family: " Avenir Next ").family == "Avenir Next")
        #expect(TextAppearance(scale: 1.3, family: "Avenir Next").with(family: nil) == TextAppearance(scale: 1.3))
    }

    @Test func theSettingIsSavedAndTheStandardOneLeavesNothingBehind() throws {
        let name = "TextAppearanceTests-\(UUID().uuidString)"
        let defaults = try #require(UserDefaults(suiteName: name))
        defer { defaults.removePersistentDomain(forName: name) }
        #expect(TextAppearance(defaults: defaults) == .standard)
        TextAppearance(scale: 1.3, family: "Avenir Next").save(to: defaults)
        #expect(TextAppearance(defaults: defaults) == TextAppearance(scale: 1.3, family: "Avenir Next"))
        TextAppearance.standard.save(to: defaults)
        #expect(defaults.object(forKey: TextAppearance.scaleKey) == nil && defaults.object(forKey: TextAppearance.familyKey) == nil)
        #expect(defaults.object(forKey: TextAppearance.transcriptFamilyKey) == nil && defaults.object(forKey: TextAppearance.codeFamilyKey) == nil)
        defaults.set("big", forKey: TextAppearance.scaleKey)
        #expect(TextAppearance(defaults: defaults).scale == 1)
        defaults.set(1.17, forKey: TextAppearance.scaleKey)
        #expect(TextAppearance(defaults: defaults).scale == 1.2)
    }

    @Test func theThreeFontsAreSavedUnderTheirOwnKeys() throws {
        let name = "TextAppearanceTests-\(UUID().uuidString)"
        let defaults = try #require(UserDefaults(suiteName: name))
        defer { defaults.removePersistentDomain(forName: name) }
        #expect(TextAppearance.familyKey == "textFontFamily")
        #expect(TextAppearance.transcriptFamilyKey == "textTranscriptFontFamily")
        #expect(TextAppearance.codeFamilyKey == "textCodeFontFamily")
        let text = TextAppearance(scale: 1.1, family: "Avenir Next", transcriptFamily: "Georgia", codeFamily: "JetBrains Mono")
        text.save(to: defaults)
        #expect(defaults.string(forKey: TextAppearance.familyKey) == "Avenir Next")
        #expect(defaults.string(forKey: TextAppearance.transcriptFamilyKey) == "Georgia")
        #expect(defaults.string(forKey: TextAppearance.codeFamilyKey) == "JetBrains Mono")
        #expect(TextAppearance(defaults: defaults) == text)
        text.with(transcriptFamily: nil).save(to: defaults)
        #expect(defaults.object(forKey: TextAppearance.transcriptFamilyKey) == nil)
        #expect(TextAppearance(defaults: defaults).resolvedTranscriptFamily == "Avenir Next")
        // A setting saved before the conversation and code fonts existed reads as it was.
        let older = "TextAppearanceTests-\(UUID().uuidString)"
        let legacy = try #require(UserDefaults(suiteName: older))
        defer { legacy.removePersistentDomain(forName: older) }
        legacy.set(1.3, forKey: TextAppearance.scaleKey)
        legacy.set("Avenir Next", forKey: TextAppearance.familyKey)
        #expect(TextAppearance(defaults: legacy) == TextAppearance(scale: 1.3, family: "Avenir Next"))
        legacy.set("   ", forKey: TextAppearance.codeFamilyKey)
        #expect(TextAppearance(defaults: legacy).codeFamily == nil)
    }
}
