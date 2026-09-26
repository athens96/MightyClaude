import Foundation

/// Result of checking whether one named resource can be resolved.
public struct ResourceCheckResult {
    public let resource: String
    public let resolvedPath: String?
    public let triedPaths: [String]
    public var found: Bool { resolvedPath != nil }

    /// A built-in (never locale-catalog) warning string, or nil when the resource was found.
    public var warning: String? {
        guard !found else { return nil }
        let tried = triedPaths.map { "  \($0)" }.joined(separator: "\n")
        return "[MightyClaude] missing resource: \(resource)\n  searched:\n\(tried)"
    }
}

/// Checks locale catalogs and the default companion pet through the same search
/// roots the running app uses, and optionally logs a built-in warning when
/// a resource cannot be resolved. An empty JSON object is treated as a miss.
public enum ResourceHealthChecker {
    // MARK: - Locale catalogs

    public static func checkCatalog(_ lang: String) -> ResourceCheckResult {
        checkCatalog(lang, overrideCandidates: nil)
    }

    /// Internal: `overrideCandidates` replaces the default search list for tests.
    static func checkCatalog(_ lang: String, overrideCandidates: [URL]?) -> ResourceCheckResult {
        let name = "\(lang).json"
        let candidates: [URL]
        if let override = overrideCandidates {
            candidates = override
        } else {
            var c: [URL] = []
            for root in BundledStyleSource.searchRoots() {
                let bundle = root.appendingPathComponent(BundledStyleSource.bundleName, isDirectory: true)
                c.append(bundle.appendingPathComponent("Contents/Resources/Locales/\(name)"))
                c.append(bundle.appendingPathComponent("Locales/\(name)"))
            }
            if let resources = Bundle.main.resourceURL {
                c.append(resources.appendingPathComponent("Locales/\(name)"))
                c.append(resources.appendingPathComponent(name))
            }
            c.append(URL(fileURLWithPath: "locales/\(lang).json"))
            candidates = c
        }
        for url in candidates {
            if let data = try? Data(contentsOf: url),
               let obj = try? JSONSerialization.jsonObject(with: data) as? [String: String],
               !obj.isEmpty {
                return ResourceCheckResult(resource: name, resolvedPath: url.path, triedPaths: candidates.map(\.path))
            }
        }
        return ResourceCheckResult(resource: name, resolvedPath: nil, triedPaths: candidates.map(\.path))
    }

    // MARK: - Default companion pet

    public static func checkDefaultPet() -> ResourceCheckResult {
        checkDefaultPet(overrideCandidates: nil)
    }

    /// Internal: `overrideCandidates` replaces the default search list for tests.
    static func checkDefaultPet(overrideCandidates: [URL]?) -> ResourceCheckResult {
        let id = "mighty-raccoon"
        let candidates: [URL]
        if let override = overrideCandidates {
            candidates = override
        } else {
            let cwd = URL(fileURLWithPath: FileManager.default.currentDirectoryPath)
            var c: [URL] = []
            if let resources = Bundle.main.resourceURL {
                c.append(resources.appendingPathComponent("pets/\(id)"))
            }
            c.append(cwd.appendingPathComponent("assets/pets/\(id)"))
            c.append(cwd.deletingLastPathComponent().deletingLastPathComponent()
                .appendingPathComponent("assets/pets/\(id)"))
            candidates = c
        }
        for url in candidates {
            if petDirectoryValid(url) {
                return ResourceCheckResult(resource: "pets/\(id)", resolvedPath: url.path,
                                           triedPaths: candidates.map(\.path))
            }
        }
        return ResourceCheckResult(resource: "pets/\(id)", resolvedPath: nil,
                                   triedPaths: candidates.map(\.path))
    }

    /// Returns true if `url` is a pet directory containing a non-empty pet.json
    /// with a non-empty `spritesheetPath`. Mirrors the validation in CompanionPet.load.
    static func petDirectoryValid(_ url: URL) -> Bool {
        let manifest = url.appendingPathComponent("pet.json")
        guard let data = try? Data(contentsOf: manifest),
              let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let path = json["spritesheetPath"] as? String, !path.isEmpty else { return false }
        return true
    }

    // MARK: - Logging

    /// Logs a built-in NSLog warning when `result` is not found; no-op otherwise.
    public static func logWarning(_ result: ResourceCheckResult) {
        guard let msg = result.warning else { return }
        NSLog("%@", msg)
    }
}
