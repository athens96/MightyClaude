import Foundation

// Language preference values stored in UserDefaults under "language".
public enum AppLanguage: String, CaseIterable {
    case system, ko, en
}

private func resolvedLanguage() -> String {
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
    let name = "\(lang).json"
    // 1. App bundle's Locales subdirectory.
    // 2. Working directory locales/ (Swift Package Manager tests).
    let candidates: [URL] = [
        Bundle.main.resourceURL.map { $0.appendingPathComponent("Locales/\(name)") },
        Bundle.main.resourceURL.map { $0.appendingPathComponent(name) },
        URL(fileURLWithPath: "locales/\(lang).json"),
    ].compactMap { $0 }
    for url in candidates {
        if let data = try? Data(contentsOf: url),
           let obj = try? JSONSerialization.jsonObject(with: data) as? [String: String] {
            return obj
        }
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
