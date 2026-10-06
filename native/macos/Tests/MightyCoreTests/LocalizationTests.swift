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
        #expect(AppLanguage.allCases.count == 5)
        #expect(AppLanguage(rawValue: "system") == .system)
        #expect(AppLanguage(rawValue: "ko") == .ko)
        #expect(AppLanguage(rawValue: "en") == .en)
        #expect(AppLanguage(rawValue: "zh") == .zh)
        #expect(AppLanguage(rawValue: "ja") == .ja)
        #expect(AppLanguage(rawValue: "zz") == nil)
    }

    @Test func systemLanguageMapsEachOSLanguageTag() {
        #expect(AppLanguage.resolvedSystem(from: ["zh-Hans"]) == .zh)
        #expect(AppLanguage.resolvedSystem(from: ["zh-Hans-CN"]) == .zh)
        // Traditional Chinese reads the Simplified copy for now.
        #expect(AppLanguage.resolvedSystem(from: ["zh-TW"]) == .zh)
        #expect(AppLanguage.resolvedSystem(from: ["zh-Hant-HK"]) == .zh)
        #expect(AppLanguage.resolvedSystem(from: ["ja-JP"]) == .ja)
        #expect(AppLanguage.resolvedSystem(from: ["ko-KR"]) == .ko)
        #expect(AppLanguage.resolvedSystem(from: ["ko_KR"]) == .ko)
        #expect(AppLanguage.resolvedSystem(from: ["en-GB"]) == .en)
        #expect(AppLanguage.resolvedSystem(from: ["fr-FR"]) == .en)
        #expect(AppLanguage.resolvedSystem(from: ["kok-IN"]) == .en)
        #expect(AppLanguage.resolvedSystem(from: []) == .en)
    }

    @Test func systemLanguageTakesTheFirstSupportedPreferredLanguage() {
        #expect(AppLanguage.resolvedSystem(from: ["ko-KR", "en-US", "zh-Hans-CN"]) == .ko)
        #expect(AppLanguage.resolvedSystem(from: ["en-US", "ko-KR", "zh-Hans-CN"]) == .en)
        #expect(AppLanguage.resolvedSystem(from: ["zh-Hans-CN", "ko-KR", "en-US"]) == .zh)
        #expect(AppLanguage.resolvedSystem(from: ["fr-FR", "de-DE", "ja-JP", "ko-KR"]) == .ja)
        #expect(AppLanguage.resolvedSystem(from: ["fr-FR", "de-DE"]) == .en)
    }

    @Test func systemPassesOverALanguageWhoseCatalogHasNoKeys() {
        let untranslated: Set<AppLanguage> = [.ja, .zh]
        let hasKeys: (AppLanguage) -> Bool = { !untranslated.contains($0) }
        #expect(AppLanguage.resolvedSystem(from: ["ja-JP", "ko-KR"], hasKeys: hasKeys) == .ko)
        #expect(AppLanguage.resolvedSystem(from: ["zh-Hans-CN", "ja-JP", "en-US", "ko-KR"], hasKeys: hasKeys) == .en)
        #expect(AppLanguage.resolvedSystem(from: ["zh-Hans-CN", "fr-FR"], hasKeys: hasKeys) == .en)
        #expect(AppLanguage.resolvedSystem(from: ["ja-JP", "ko-KR"], hasKeys: { _ in true }) == .ja)
    }

    @Test func systemPassesOverTheBundledCatalogsThatHaveNoKeysYet() {
        // Reads the bundled files, so it holds before and after the translations land.
        let jaHasKeys = ResourceHealthChecker.checkCatalog("ja").found
        withLanguage("system") {
            LocaleOverride.$preferredLanguages.withValue(["ja-JP", "ko-KR"]) {
                if jaHasKeys { #expect(L("guidedPanel.cancelButton") != "취소") }
                else { #expect(L("guidedPanel.cancelButton") == "취소") }
            }
        }
    }

    @Test func choosingATranslationStillReadsIt() {
        withLanguage("ja") {
            // An empty translation shows English; a filled one shows its own value, never Korean.
            #expect(L("guidedPanel.cancelButton") != "취소")
        }
    }

    @Test func translationCatalogsExistEvenWhileEmpty() {
        for lang in AppLanguage.allCases where lang != .system {
            #expect(ResourceHealthChecker.checkCatalog(lang.rawValue, allowEmpty: !lang.isComplete).found, "\(lang.rawValue).json")
        }
    }

    @Test func systemPreferenceReadsTheOSPreferredLanguages() {
        withLanguage("system") {
            LocaleOverride.$preferredLanguages.withValue(["fr-FR", "ko-KR"]) {
                #expect(L("guidedPanel.cancelButton") == "취소")
            }
            LocaleOverride.$preferredLanguages.withValue(["fr-FR"]) {
                #expect(L("guidedPanel.cancelButton") == "Cancel")
            }
        }
    }

    @Test func lookupOrderFallsBackToEnglishThenKorean() {
        #expect(AppLanguage.ko.lookupOrder == [.ko])
        #expect(AppLanguage.en.lookupOrder == [.en, .ko])
        #expect(AppLanguage.zh.lookupOrder == [.zh, .en, .ko])
        #expect(AppLanguage.ja.lookupOrder == [.ja, .en, .ko])
        let zh = ["both": "中文"], en = ["both": "English", "enOnly": "English only"], ko = ["both": "한국어", "enOnly": "영어만", "koOnly": "한국어만"]
        #expect(localeTemplate("both", in: [zh, en, ko]) == "中文")
        #expect(localeTemplate("enOnly", in: [zh, en, ko]) == "English only")
        #expect(localeTemplate("koOnly", in: [zh, en, ko]) == "한국어만")
        #expect(localeTemplate("none", in: [zh, en, ko]) == "none")
    }

    @Test func translationsReadEveryKeyAndFillPlaceholders() {
        for lang in ["zh", "ja"] {
            withLanguage(lang) {
                #expect(L("guidedPanel.cancelButton") != "guidedPanel.cancelButton", "\(lang): no value reached")
                let result = L("settings.appUpdate.availableTemplate", ["version": "1.2.3"])
                #expect(result.contains("1.2.3") && !result.contains("{version}"), "\(lang): \(result)")
            }
        }
    }

    @Test func pickerOffersEveryLanguageNamedInItsOwnLanguage() {
        #expect(AppLanguage.pickerChoices == [.system, .ko, .en, .zh, .ja])
        for lang in ["ko", "en", "zh", "ja"] {
            withLanguage(lang) {
                #expect(L(AppLanguage.ko.labelKey) == "한국어")
                #expect(L(AppLanguage.en.labelKey) == "English")
                #expect(L(AppLanguage.zh.labelKey) == "简体中文")
                #expect(L(AppLanguage.ja.labelKey) == "日本語")
                #expect(L(AppLanguage.system.labelKey) != AppLanguage.system.labelKey)
            }
        }
        withLanguage("ko") { #expect(L(AppLanguage.system.labelKey) == "시스템") }
        withLanguage("en") { #expect(L(AppLanguage.system.labelKey) == "System") }
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
            "settings.display.languageChinese",
            "settings.display.languageJapanese",
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
