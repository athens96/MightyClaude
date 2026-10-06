import Foundation

// Language preference values stored in UserDefaults under "language".
public enum AppLanguage: String, CaseIterable {
    case system, ko, en
}

/// A language forced for the current task only, so a test can read one copy
/// without touching the process-wide preference other suites read at the same
/// time. nil follows the preference.
public enum LocaleOverride {
    @TaskLocal public static var language: AppLanguage?
    /// The defaults the language preference is read from for the current task
    /// only (a test's private suite); nil reads `UserDefaults.standard`.
    @TaskLocal public static var defaults: UserDefaults?
}

private func resolvedLanguage() -> String {
    if let forced = LocaleOverride.language, forced != .system { return forced.rawValue }
    let pref = (LocaleOverride.defaults ?? .standard).string(forKey: "language") ?? ""
    switch AppLanguage(rawValue: pref) ?? .system {
    case .ko: return "ko"
    case .en: return "en"
    case .system:
        return Locale.current.identifier.hasPrefix("ko") ? "ko" : "en"
    }
}

private typealias Catalog = [String: String]

private func loadCatalog(_ lang: String) -> Catalog {
    // ResourceHealthChecker owns the search-root logic so --verify-resources and
    // the running app walk exactly the same candidate list. An empty JSON object
    // is treated as a miss (same rule as the guard).
    let result = ResourceHealthChecker.checkCatalog(lang)
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

private var _ko: Catalog?
private var _en: Catalog?
private var _lock = NSLock()

private func catalogs() -> (chosen: Catalog, korean: Catalog) {
    _lock.lock()
    defer { _lock.unlock() }
    if _ko == nil { _ko = loadCatalog("ko") }
    if _en == nil { _en = loadCatalog("en") }
    let lang = resolvedLanguage()
    let chosen = lang == "ko" ? _ko! : _en!
    return (chosen, _ko!)
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

/// Looks up a locale key in the current language, falls back to Korean, then to the key itself.
public func L(_ key: String, _ subs: [String: String] = [:]) -> String {
    let (chosen, korean) = catalogs()
    let template = chosen[key] ?? korean[key] ?? key
    return fill(template, subs)
}

/// Clears cached catalogs so the next call to L() re-reads from disk.
/// Call on app launch after the language preference changes.
public func resetLocaleCache() {
    _lock.lock()
    defer { _lock.unlock() }
    _ko = nil
    _en = nil
}

public extension AppLanguage {
    static let systemInterfaceKey = "AppleLanguages"
    private static let appliedMarker = "languageAppliedToSystemInterface"
    private static let previousValue = "languagePreviousSystemInterface"

    /// AppKit draws its own menus (the app menu, Edit, Window, Help) in the
    /// language it reads from AppleLanguages once, at launch. A choice of 한국어
    /// or English goes there too; 시스템 gives back what was there before,
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
        case .ko, .en:
            if !applied, let own = own as? [String] { defaults.set(own, forKey: previousValue) }
            defaults.set([choice.rawValue], forKey: systemInterfaceKey)
            defaults.set(true, forKey: appliedMarker)
        }
    }
}
