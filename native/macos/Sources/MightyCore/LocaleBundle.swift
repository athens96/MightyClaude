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
}

private func resolvedLanguage() -> String {
    if let forced = LocaleOverride.language, forced != .system { return forced.rawValue }
    let pref = UserDefaults.standard.string(forKey: "language") ?? ""
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

private func fill(_ template: String, _ subs: [String: String]) -> String {
    guard !subs.isEmpty else { return template }
    var result = template
    for (key, value) in subs {
        result = result.replacingOccurrences(of: "{\(key)}", with: value)
    }
    return result
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
