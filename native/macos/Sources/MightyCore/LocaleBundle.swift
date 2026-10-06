import Foundation

// Language preference values stored in UserDefaults under "language".
public enum AppLanguage: String, CaseIterable {
    case system, ko, en, zh, ja
}

public extension AppLanguage {
    /// The Settings picker's choices, in the order it shows them.
    static let pickerChoices: [AppLanguage] = [.system, .ko, .en, .zh, .ja]

    /// The locale key of this choice's picker label. A language is named in its
    /// own language (the same words in every catalog); only "system" is translated.
    var labelKey: String {
        switch self {
        case .system: "settings.display.languageSystem"
        case .ko: "settings.display.languageKorean"
        case .en: "settings.display.languageEnglish"
        case .zh: "settings.display.languageChinese"
        case .ja: "settings.display.languageJapanese"
        }
    }

    /// ko and en carry every key; zh and ja are translations that may still miss some.
    var isComplete: Bool { self == .ko || self == .en }

    /// The language an OS language tag reads, or nil when the app has no copy of it.
    /// Every Chinese tag reads the Simplified copy for now, zh-Hant, zh-TW and zh-HK included.
    static func forTag(_ tag: String) -> AppLanguage? {
        let primary = tag.lowercased().split(whereSeparator: { $0 == "-" || $0 == "_" }).first.map(String.init) ?? ""
        switch primary {
        case "ko": return .ko
        case "en": return .en
        case "zh": return .zh
        case "ja": return .ja
        default: return nil
        }
    }

    /// What "system" reads: the first of the OS's preferred languages the app has a
    /// copy of with at least one key (`hasKeys`), so an untranslated catalog is passed
    /// over for the next preferred language; English when none qualifies.
    static func resolvedSystem(from preferred: [String], hasKeys: (AppLanguage) -> Bool = { _ in true }) -> AppLanguage {
        preferred.lazy.compactMap(forTag).first(where: hasKeys) ?? .en
    }

    /// The catalogs a lookup reads, in order, before it gives back the key itself.
    /// ko and en keep their own rule (ko alone; en then ko); a translation falls back to en, then ko.
    var lookupOrder: [AppLanguage] {
        switch self {
        case .ko: [.ko]
        case .en: [.en, .ko]
        case .zh, .ja: [self, .en, .ko]
        case .system: [.en, .ko]
        }
    }
}

/// A language forced for the current task only, so a test can read one copy
/// without touching the process-wide preference other suites read at the same
/// time. nil follows the preference.
public enum LocaleOverride {
    @TaskLocal public static var language: AppLanguage?
    /// The defaults the language preference is read from for the current task
    /// only (a test's private suite); nil reads `UserDefaults.standard`.
    @TaskLocal public static var defaults: UserDefaults?
    /// The OS's preferred languages for the current task only; nil reads `Locale.preferredLanguages`.
    /// Set, it also bypasses the cached system language.
    @TaskLocal public static var preferredLanguages: [String]?
}

private func resolvedLanguage() -> AppLanguage {
    if let forced = LocaleOverride.language, forced != .system { return forced }
    let pref = (LocaleOverride.defaults ?? .standard).string(forKey: "language") ?? ""
    let choice = AppLanguage(rawValue: pref) ?? .system
    guard choice == .system else { return choice }
    if let preferred = LocaleOverride.preferredLanguages {
        return AppLanguage.resolvedSystem(from: preferred, hasKeys: { lang in
            _lock.lock(); defer { _lock.unlock() }
            return !catalogLocked(lang).isEmpty
        })
    }
    _ = localeChangeObserver
    _lock.lock()
    defer { _lock.unlock() }
    if let cached = _systemLanguage { return cached }
    let resolved = AppLanguage.resolvedSystem(from: Locale.preferredLanguages, hasKeys: { !catalogLocked($0).isEmpty })
    _systemLanguage = resolved
    return resolved
}

/// Forgets the cached system language when the OS language or region changes.
private let localeChangeObserver: NSObjectProtocol = NotificationCenter.default.addObserver(
    forName: NSLocale.currentLocaleDidChangeNotification, object: nil, queue: nil
) { _ in
    _lock.lock(); defer { _lock.unlock() }
    _systemLanguage = nil
}

private typealias Catalog = [String: String]

private func loadCatalog(_ lang: String) -> Catalog {
    // ResourceHealthChecker owns the search-root logic so --verify-resources and
    // the running app walk exactly the same candidate list. A complete language's
    // empty JSON object is treated as a miss (same rule as the guard); a translation
    // may still be an empty object, whose keys then read English.
    let result = ResourceHealthChecker.checkCatalog(lang, allowEmpty: !(AppLanguage(rawValue: lang)?.isComplete ?? true))
    if !result.found {
        ResourceHealthChecker.logWarning(result)
        return [:]
    }
    if let path = result.resolvedPath,
       let data = try? Data(contentsOf: URL(fileURLWithPath: path)),
       let obj = try? JSONSerialization.jsonObject(with: data) as? [String: String] {
        return obj
    }
    return [:]
}

private var _catalogs: [AppLanguage: Catalog] = [:]
private var _systemLanguage: AppLanguage?
private var _lock = NSLock()

/// The catalog of `lang`, loading it once. The caller holds `_lock`.
private func catalogLocked(_ lang: AppLanguage) -> Catalog {
    if let cached = _catalogs[lang] { return cached }
    let loaded = loadCatalog(lang.rawValue)
    _catalogs[lang] = loaded
    return loaded
}

/// The catalogs of `language`'s lookup order, loading each one once.
private func catalogs(for language: AppLanguage) -> [Catalog] {
    _lock.lock()
    defer { _lock.unlock() }
    return language.lookupOrder.map(catalogLocked)
}

/// The first catalog in `catalogs` that has `key`, else the key itself.
func localeTemplate(_ key: String, in catalogs: [[String: String]]) -> String {
    for catalog in catalogs { if let value = catalog[key] { return value } }
    return key
}

/// One pass over the template: a substituted value is never scanned again, so
/// text that itself contains `{name}` (a manifest's glob, a user's title) is
/// shown as written whatever order the dictionary yields its keys in.
func fill(_ template: String, _ subs: [String: String]) -> String {
    guard !subs.isEmpty else { return template }
    var result = ""
    var rest = template[...]
    while let open = rest.firstIndex(of: "{") {
        result += rest[..<open]
        let afterOpen = rest.index(after: open)
        if let close = rest[afterOpen...].firstIndex(of: "}"), let value = subs[String(rest[afterOpen..<close])] {
            result += value
            rest = rest[rest.index(after: close)...]
        } else {
            result += "{"
            rest = rest[afterOpen...]
        }
    }
    return result + rest
}

/// Looks up a locale key in the current language and then its fallbacks
/// (`AppLanguage.lookupOrder`), then gives back the key itself.
public func L(_ key: String, _ subs: [String: String] = [:]) -> String {
    fill(localeTemplate(key, in: catalogs(for: resolvedLanguage())), subs)
}

/// Clears cached catalogs and the cached system language so the next call to L()
/// re-reads them. Call on app launch after the language preference changes.
public func resetLocaleCache() {
    _lock.lock()
    defer { _lock.unlock() }
    _catalogs = [:]
    _systemLanguage = nil
}

public extension AppLanguage {
    static let systemInterfaceKey = "AppleLanguages"
    private static let appliedMarker = "languageAppliedToSystemInterface"
    private static let previousValue = "languagePreviousSystemInterface"

    /// AppKit draws its own menus (the app menu, Edit, Window, Help) in the
    /// language it reads from AppleLanguages once, at launch. A chosen language
    /// goes there too; System gives back what was there before,
    /// including a language set for this app in System Settings. `domain` is
    /// the defaults domain `defaults` writes to.
    static func applyToSystemInterface(_ defaults: UserDefaults = .standard, domain: String? = Bundle.main.bundleIdentifier) {
        let own = domain.flatMap { defaults.persistentDomain(forName: $0)?[systemInterfaceKey] }
        let choice = AppLanguage(rawValue: defaults.string(forKey: "language") ?? "") ?? .system
        let applied = defaults.bool(forKey: appliedMarker)
        switch choice {
        case .system:
            guard applied else { return }
            if let previous = defaults.array(forKey: previousValue) { defaults.set(previous, forKey: systemInterfaceKey) }
            else { defaults.removeObject(forKey: systemInterfaceKey) }
            defaults.removeObject(forKey: previousValue); defaults.removeObject(forKey: appliedMarker)
        case .ko, .en, .zh, .ja:
            if !applied, let own = own as? [String] { defaults.set(own, forKey: previousValue) }
            defaults.set([choice.systemInterfaceTag], forKey: systemInterfaceKey)
            defaults.set(true, forKey: appliedMarker)
        }
    }

    /// The AppleLanguages entry of a chosen language: Chinese is the Simplified script.
    private var systemInterfaceTag: String { self == .zh ? "zh-Hans" : rawValue }
}
