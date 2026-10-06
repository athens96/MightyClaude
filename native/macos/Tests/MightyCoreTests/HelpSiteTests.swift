import Foundation
import Testing
@testable import MightyCore

/// The same file Windows (`HelpSiteVerification`) and the phone (`help-site.test.ts`)
/// read: every app opens the user guide at the same address in its display language.
struct HelpSiteTests {
    struct Fixture: Decodable {
        struct Case: Decodable { let language: String; let url: String }
        let base: String
        let cases: [Case]
    }

    static let fixture: URL = {
        var url = URL(fileURLWithPath: #filePath)
        for _ in 0..<5 { url.deleteLastPathComponent() }
        return url.appendingPathComponent("native/contracts/fixtures/help-site.json")
    }()

    @Test func everyLanguageOpensItsFolderOfTheSharedBase() throws {
        let fixture = try JSONDecoder().decode(Fixture.self, from: Data(contentsOf: Self.fixture))
        #expect(HelpSite.base == fixture.base)
        #expect(fixture.cases.map(\.language) == ["ko", "en", "zh", "ja"])
        for item in fixture.cases {
            let language = try #require(AppLanguage(rawValue: item.language))
            #expect(HelpSite.url(for: language).absoluteString == item.url, "\(item.language)")
        }
        #expect(HelpSite.url(for: .system) == HelpSite.url(for: .en))
    }

    @Test func theCurrentGuideFollowsTheDisplayLanguage() {
        for language in [AppLanguage.ko, .en, .zh, .ja] {
            LocaleOverride.$language.withValue(language) {
                #expect(AppLanguage.current == language)
                #expect(HelpSite.current == HelpSite.url(for: language))
            }
        }
        // "system" opens the folder of the language it resolves to, not a "system" folder.
        let name = "HelpSiteTests-" + UUID().uuidString
        let defaults = UserDefaults(suiteName: name)!
        defer { defaults.removePersistentDomain(forName: name) }
        defaults.set("system", forKey: "language")
        LocaleOverride.$defaults.withValue(defaults) {
            LocaleOverride.$preferredLanguages.withValue(["ja-JP", "en-US"]) {
                #expect(HelpSite.current.absoluteString.hasSuffix("/help/ja/"))
            }
            LocaleOverride.$preferredLanguages.withValue(["fr-FR"]) {
                #expect(HelpSite.current.absoluteString.hasSuffix("/help/en/"))
            }
        }
    }
}
