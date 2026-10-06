import Foundation

/// The user guide `scripts/help/build.mjs` publishes, one folder per language
/// (`<base>/<lang>/`). The address is pinned by `native/contracts/fixtures/help-site.json`,
/// which Windows (`HelpSite.cs`) and the phone (`help-site.ts`) are held to as well.
public enum HelpSite {
    public static let base = "https://pub-fd035e0a9ad7411f8d8d8963cc2b9702.r2.dev/mightyclaude/help"

    /// The guide's folder in `language`; "system" reads the English folder.
    public static func url(for language: AppLanguage) -> URL {
        let folder = language == .system ? AppLanguage.en.rawValue : language.rawValue
        return URL(string: base + "/" + folder + "/")!
    }

    /// The guide in the language the app shows now.
    public static var current: URL { url(for: .current) }
}
