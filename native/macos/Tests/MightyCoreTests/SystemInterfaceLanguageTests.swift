import Foundation
import Testing
@testable import MightyCore

/// The menus AppKit draws follow the app's language choice, and 시스템 gives
/// back whatever the app's own defaults said before.
@Suite struct SystemInterfaceLanguageTests {
    private func fresh() -> (UserDefaults, String) {
        let name = "mighty-language-" + UUID().uuidString
        return (UserDefaults(suiteName: name)!, name)
    }
    private func own(_ defaults: UserDefaults, _ name: String) -> [String]? {
        defaults.persistentDomain(forName: name)?[AppLanguage.systemInterfaceKey] as? [String]
    }

    @Test func aChosenLanguageIsWrittenAndSystemTakesItBack() {
        let (defaults, name) = fresh()
        defer { defaults.removePersistentDomain(forName: name) }
        AppLanguage.applyToSystemInterface(defaults, domain: name)
        #expect(own(defaults, name) == nil)
        defaults.set("en", forKey: "language")
        AppLanguage.applyToSystemInterface(defaults, domain: name)
        #expect(own(defaults, name) == ["en"])
        defaults.set("ko", forKey: "language")
        AppLanguage.applyToSystemInterface(defaults, domain: name)
        #expect(own(defaults, name) == ["ko"])
        defaults.set("system", forKey: "language")
        AppLanguage.applyToSystemInterface(defaults, domain: name)
        #expect(own(defaults, name) == nil)
    }

    @Test func aLanguageSetForTheAppInSystemSettingsSurvives() {
        let (defaults, name) = fresh()
        defer { defaults.removePersistentDomain(forName: name) }
        defaults.set(["ja"], forKey: AppLanguage.systemInterfaceKey)
        AppLanguage.applyToSystemInterface(defaults, domain: name)
        #expect(own(defaults, name) == ["ja"])
        defaults.set("en", forKey: "language")
        AppLanguage.applyToSystemInterface(defaults, domain: name)
        AppLanguage.applyToSystemInterface(defaults, domain: name)
        #expect(own(defaults, name) == ["en"])
        defaults.set("system", forKey: "language")
        AppLanguage.applyToSystemInterface(defaults, domain: name)
        #expect(own(defaults, name) == ["ja"])
    }
}
