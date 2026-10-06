import Foundation
import Testing
@testable import MightyCore

/// The preference path is read from a private defaults suite for each test's
/// own task (`LocaleOverride.defaults`), never from `UserDefaults.standard`,
/// so suites that read `L()` at the same time are never switched under them.
struct LocalizationTests {

    /// Runs `body` with the "language" preference set to `lang` (nil: unset)
    /// in a private suite that only this task reads.
    private func withLanguage(_ lang: String?, _ body: () throws -> Void) rethrows {
        let name = "mighty-locale-tests-" + UUID().uuidString
        let defaults = UserDefaults(suiteName: name)!
        defer { defaults.removePersistentDomain(forName: name) }
        if let lang { defaults.set(lang, forKey: "language") }
        try LocaleOverride.$defaults.withValue(defaults) { try body() }
    }

    @Test func appLanguageEnumCoversExpectedCases() {
        #expect(AppLanguage.allCases.count == 3)
        #expect(AppLanguage(rawValue: "system") == .system)
        #expect(AppLanguage(rawValue: "ko") == .ko)
        #expect(AppLanguage(rawValue: "en") == .en)
        #expect(AppLanguage(rawValue: "zz") == nil)
    }

    @Test func lookupKoreanReturnsKoreanValue() {
        withLanguage("ko") {
            #expect(L("guidedPanel.cancelButton") == "취소")
            #expect(L("guidedPanel.installButton") == "설치")
        }
    }

    @Test func lookupEnglishReturnsEnglishValue() {
        withLanguage("en") {
            #expect(L("guidedPanel.cancelButton") == "Cancel")
            #expect(L("guidedPanel.installButton") == "Install")
        }
    }

    @Test func missingKeyInBothCatalogsReturnsKeyItself() {
        withLanguage("en") {
            let key = "completely.unknown.key"
            #expect(L(key) == key)
        }
    }

    @Test func placeholderSubstitutionKorean() {
        withLanguage("ko") {
            let result = L("settings.appUpdate.availableTemplate", ["version": "1.2.3"])
            #expect(result == "새 버전 1.2.3 이 있습니다.")
        }
    }

    @Test func placeholderSubstitutionEnglish() {
        withLanguage("en") {
            let result = L("settings.appUpdate.availableTemplate", ["version": "1.2.3"])
            #expect(result == "Version 1.2.3 is available.")
        }
    }

    @Test func settingsDisplayKeysExistInBothCatalogs() {
        let keys = [
            "settings.display.sectionTitle",
            "settings.display.languageLabel",
            "settings.display.languageSystem",
            "settings.display.languageKorean",
            "settings.display.languageEnglish",
        ]
        for lang in ["ko", "en"] {
            withLanguage(lang) {
                for key in keys {
                    #expect(L(key) != key, "\(lang): key '\(key)' missing from catalog")
                }
            }
        }
    }

    @Test func guidedPanelKeysExistInBothCatalogs() {
        let keys = [
            "guidedPanel.approvalRequired",
            "guidedPanel.cancelButton",
            "guidedPanel.installButton",
            "guidedPanel.phasesAccessibility",
            "guidedPanel.recheckButton",
        ]
        for lang in ["ko", "en"] {
            withLanguage(lang) {
                for key in keys {
                    #expect(L(key) != key, "\(lang): key '\(key)' missing from catalog")
                }
            }
        }
    }

    @Test func runSettingsAndPermissionKeysExistInBothCatalogs() {
        let keys = [
            "settings.run.title",
            "settings.run.webSearchLabel",
            "settings.run.limitsTitle",
            "settings.run.applyButton",
            "permission.label.plan",
            "permission.claude.default",
            "permission.other.default",
        ]
        for lang in ["ko", "en"] {
            withLanguage(lang) {
                for key in keys {
                    #expect(L(key) != key, "\(lang): key '\(key)' missing from catalog")
                }
            }
        }
    }

    @Test func templatePlaceholdersAreSubstituted() {
        withLanguage("ko") {
            let result = L("graph.header.agents", ["n": "3"])
            #expect(result == "하위 에이전트 3")
            #expect(!result.contains("{n}"))
        }
    }

    @Test func resetCacheClearsLoadedCatalogs() {
        withLanguage("ko") {
            let first = L("guidedPanel.cancelButton")
            resetLocaleCache()
            let second = L("guidedPanel.cancelButton")
            #expect(first == second)
        }
    }
}
