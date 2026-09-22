import Foundation
import Testing
@testable import MightyCore

// Tests modify shared global state (UserDefaults + locale cache) so must run serially.
@Suite(.serialized)
struct LocalizationTests {

    private func setLanguage(_ lang: String?) {
        if let lang { UserDefaults.standard.set(lang, forKey: "language") }
        else { UserDefaults.standard.removeObject(forKey: "language") }
        resetLocaleCache()
    }

    @Test func appLanguageEnumCoversExpectedCases() {
        #expect(AppLanguage.allCases.count == 3)
        #expect(AppLanguage(rawValue: "system") == .system)
        #expect(AppLanguage(rawValue: "ko") == .ko)
        #expect(AppLanguage(rawValue: "en") == .en)
        #expect(AppLanguage(rawValue: "zz") == nil)
    }

    @Test func lookupKoreanReturnsKoreanValue() {
        setLanguage("ko")
        defer { setLanguage(nil) }
        #expect(L("guidedPanel.cancelButton") == "취소")
        #expect(L("guidedPanel.installButton") == "설치")
    }

    @Test func lookupEnglishReturnsEnglishValue() {
        setLanguage("en")
        defer { setLanguage(nil) }
        #expect(L("guidedPanel.cancelButton") == "Cancel")
        #expect(L("guidedPanel.installButton") == "Install")
    }

    @Test func missingKeyInBothCatalogsReturnsKeyItself() {
        setLanguage("en")
        defer { setLanguage(nil) }
        let key = "completely.unknown.key"
        #expect(L(key) == key)
    }

    @Test func placeholderSubstitutionKorean() {
        setLanguage("ko")
        defer { setLanguage(nil) }
        let result = L("settings.appUpdate.availableTemplate", ["version": "1.2.3"])
        #expect(result == "새 버전 1.2.3 이 있습니다.")
    }

    @Test func placeholderSubstitutionEnglish() {
        setLanguage("en")
        defer { setLanguage(nil) }
        let result = L("settings.appUpdate.availableTemplate", ["version": "1.2.3"])
        #expect(result == "Version 1.2.3 is available.")
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
            setLanguage(lang)
            defer { setLanguage(nil) }
            for key in keys {
                #expect(L(key) != key, "\(lang): key '\(key)' missing from catalog")
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
            setLanguage(lang)
            defer { setLanguage(nil) }
            for key in keys {
                #expect(L(key) != key, "\(lang): key '\(key)' missing from catalog")
            }
        }
    }

    @Test func runSettingsAndPermissionKeysExistInBothCatalogs() {
        let keys = [
            "settings.run.title",
            "settings.run.webSearchLabel",
            "settings.run.limitsTitle",
            "settings.run.applyButton",
            "settings.run.remoteAccountTemplate",
            "permission.label.plan",
            "permission.claude.default",
            "permission.other.default",
        ]
        for lang in ["ko", "en"] {
            setLanguage(lang)
            defer { setLanguage(nil) }
            for key in keys {
                #expect(L(key) != key, "\(lang): key '\(key)' missing from catalog")
            }
        }
    }

    @Test func runSettingsTemplateSubstitutesHost() {
        setLanguage("ko")
        defer { setLanguage(nil) }
        let result = L("settings.run.remoteAccountTemplate", ["host": "mac-mini"])
        #expect(result == "mac-mini의 계정 권한으로 실행합니다.")
        #expect(!result.contains("{host}"))
    }

    @Test func resetCacheClearsLoadedCatalogs() {
        setLanguage("ko")
        defer { setLanguage(nil) }
        let first = L("guidedPanel.cancelButton")
        resetLocaleCache()
        setLanguage("ko")
        let second = L("guidedPanel.cancelButton")
        #expect(first == second)
    }
}
