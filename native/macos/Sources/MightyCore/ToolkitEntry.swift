import Foundation

public enum ToolkitPlatform: String, Sendable, Equatable, CaseIterable {
    case macOS, windows
}

public enum ToolkitPackageManager: String, Sendable, Equatable, CaseIterable {
    case brew, npm, winget
}

public enum ToolkitEntrySource: String, Sendable, Equatable { case bundled, user }

public enum ToolkitInstallSpec: Sendable, Equatable {
    case plugin(source: String, pluginID: String)
    case mcp(name: String, executable: String, args: [String])
    case skill(url: String)
    case package(manager: ToolkitPackageManager, name: String, executable: String? = nil)
    case repoScript(url: String, ref: String, scriptPath: String)

    /// The set of platforms on which this entry is listed and run.
    public var platforms: Set<ToolkitPlatform> {
        switch self {
        case .plugin, .mcp, .skill: return [.macOS, .windows]
        case .package(let manager, _, _):
            switch manager {
            case .brew: return [.macOS]
            case .npm: return [.macOS, .windows]
            case .winget: return [.windows]
            }
        case .repoScript: return [.macOS]
        }
    }
}

public struct ToolkitEntry: Sendable, Equatable {
    public var entryId: String
    public var displayName: String
    public var source: ToolkitEntrySource
    public var install: ToolkitInstallSpec

    public init(entryId: String, displayName: String, source: ToolkitEntrySource, install: ToolkitInstallSpec) {
        self.entryId = entryId; self.displayName = displayName
        self.source = source; self.install = install
    }
}

public enum ToolkitBundled {
    public static let entries: [ToolkitEntry] = [
        ToolkitEntry(
            entryId: "mighty-styles",
            displayName: "mighty-styles",
            source: .bundled,
            install: .plugin(source: "athens96/mighty-styles", pluginID: "mighty-styles@mighty-styles")
        ),
    ]
}

public struct ToolkitDecodeFailure: Error, Sendable {
    public let message: String
    public init(_ message: String) { self.message = message }
}

public enum ToolkitEntryDecoder {
    public static func decode(_ object: [String: Any]) throws -> ToolkitEntry {
        let allowedRoot: Set<String> = ["id", "displayName", "install"]
        for key in object.keys where !allowedRoot.contains(key) {
            throw ToolkitDecodeFailure("Unknown field: \(key)")
        }
        guard let id = object["id"] as? String, !id.isEmpty, id.utf8.count <= 128, validIdentifier(id) else {
            throw ToolkitDecodeFailure("Invalid or missing 'id'")
        }
        guard let displayName = object["displayName"] as? String, !displayName.isEmpty, displayName.count <= 120 else {
            throw ToolkitDecodeFailure("Invalid or missing 'displayName'")
        }
        guard let installObject = object["install"] as? [String: Any] else {
            throw ToolkitDecodeFailure("Missing or invalid 'install' spec")
        }
        let spec = try decodeInstallSpec(installObject)
        return ToolkitEntry(entryId: id, displayName: displayName, source: .user, install: spec)
    }

    // MARK: - Install spec

    private static func decodeInstallSpec(_ object: [String: Any]) throws -> ToolkitInstallSpec {
        guard let kind = object["kind"] as? String else {
            throw ToolkitDecodeFailure("Missing 'kind' in install spec")
        }
        switch kind {
        case "plugin":     return try decodePlugin(object)
        case "mcp":        return try decodeMCP(object)
        case "skill":      return try decodeSkill(object)
        case "package":    return try decodePackage(object)
        case "repoScript": return try decodeRepoScript(object)
        default: throw ToolkitDecodeFailure("Unknown install kind: \(kind)")
        }
    }

    private static func decodePlugin(_ object: [String: Any]) throws -> ToolkitInstallSpec {
        let allowed: Set<String> = ["kind", "source", "pluginID"]
        for key in object.keys where !allowed.contains(key) { throw ToolkitDecodeFailure("Unknown field in plugin spec: \(key)") }
        guard let source = object["source"] as? String, validPluginSource(source) else {
            throw ToolkitDecodeFailure("Invalid or missing plugin 'source' (owner/repo or https URL)")
        }
        guard let pluginID = object["pluginID"] as? String, validPluginID(pluginID) else {
            throw ToolkitDecodeFailure("Invalid or missing 'pluginID' (must be name@marketplace)")
        }
        return .plugin(source: source, pluginID: pluginID)
    }

    private static func decodeMCP(_ object: [String: Any]) throws -> ToolkitInstallSpec {
        let allowed: Set<String> = ["kind", "name", "executable", "args"]
        for key in object.keys where !allowed.contains(key) { throw ToolkitDecodeFailure("Unknown field in mcp spec: \(key)") }
        guard let name = object["name"] as? String, validIdentifier(name) else {
            throw ToolkitDecodeFailure("Invalid or missing MCP server 'name'")
        }
        guard let executable = object["executable"] as? String, validExecutable(executable) else {
            throw ToolkitDecodeFailure("Invalid or missing MCP 'executable' (absolute path or bare name)")
        }
        let args: [String]
        if let raw = object["args"] {
            guard let arr = raw as? [Any], arr.count <= 64 else { throw ToolkitDecodeFailure("MCP 'args' must be an array (max 64)") }
            args = try arr.map { elem -> String in
                guard let s = elem as? String, !s.contains("\0") else { throw ToolkitDecodeFailure("Invalid MCP arg element") }
                return s
            }
        } else { args = [] }
        return .mcp(name: name, executable: executable, args: args)
    }

    private static func decodeSkill(_ object: [String: Any]) throws -> ToolkitInstallSpec {
        let allowed: Set<String> = ["kind", "url"]
        for key in object.keys where !allowed.contains(key) { throw ToolkitDecodeFailure("Unknown field in skill spec: \(key)") }
        guard let url = object["url"] as? String, validHttpsURL(url) else {
            throw ToolkitDecodeFailure("Invalid or missing skill 'url' (must start with https://)")
        }
        return .skill(url: url)
    }

    private static func decodePackage(_ object: [String: Any]) throws -> ToolkitInstallSpec {
        guard let managerStr = object["manager"] as? String, let manager = ToolkitPackageManager(rawValue: managerStr) else {
            throw ToolkitDecodeFailure("Invalid or missing 'manager' (must be brew, npm, or winget)")
        }
        let allowed: Set<String> = manager == .winget
            ? ["kind", "manager", "name", "executable"]
            : ["kind", "manager", "name"]
        for key in object.keys where !allowed.contains(key) { throw ToolkitDecodeFailure("Unknown field in package spec: \(key)") }
        if manager == .winget {
            guard let name = object["name"] as? String, validWingetName(name) else {
                throw ToolkitDecodeFailure("Invalid or missing package 'name' for winget")
            }
            guard let executable = object["executable"] as? String, validWingetExecutable(executable) else {
                throw ToolkitDecodeFailure("Missing or invalid 'executable' for winget (required bare filename)")
            }
            return .package(manager: .winget, name: name, executable: executable)
        } else {
            guard let name = object["name"] as? String, validPackageName(name) else {
                throw ToolkitDecodeFailure("Invalid or missing package 'name'")
            }
            return .package(manager: manager, name: name)
        }
    }

    private static func decodeRepoScript(_ object: [String: Any]) throws -> ToolkitInstallSpec {
        if object["arguments"] != nil {
            throw ToolkitDecodeFailure("repoScript must not have an 'arguments' field")
        }
        let allowed: Set<String> = ["kind", "url", "ref", "scriptPath"]
        for key in object.keys where !allowed.contains(key) { throw ToolkitDecodeFailure("Unknown field in repoScript spec: \(key)") }
        guard let url = object["url"] as? String, validHttpsURL(url) else {
            throw ToolkitDecodeFailure("Invalid or missing repoScript 'url' (must start with https://)")
        }
        guard let ref = object["ref"] as? String, validRef(ref) else {
            throw ToolkitDecodeFailure("Invalid or missing 'ref' (must be 40-hex SHA or tag)")
        }
        guard let scriptPath = object["scriptPath"] as? String, validScriptPath(scriptPath) else {
            throw ToolkitDecodeFailure("Invalid or missing 'scriptPath' (relative, no ..)")
        }
        return .repoScript(url: url, ref: ref, scriptPath: scriptPath)
    }

    // MARK: - Validation helpers

    public static func validIdentifier(_ value: String) -> Bool {
        value.utf8.count <= 128 &&
        value.range(of: #"\A[A-Za-z0-9][A-Za-z0-9._-]*\z"#, options: .regularExpression) != nil
    }

    static func validPluginSource(_ value: String) -> Bool {
        guard !value.isEmpty, !value.contains("\0"), value.utf8.count <= 2048 else { return false }
        if value.range(of: #"\A[A-Za-z0-9][A-Za-z0-9._-]*/[A-Za-z0-9][A-Za-z0-9._-]*\z"#, options: .regularExpression) != nil { return true }
        return value.hasPrefix("https://")
    }

    static func validPluginID(_ value: String) -> Bool {
        let parts = value.split(separator: "@", omittingEmptySubsequences: false)
        guard parts.count == 2 else { return false }
        return validIdentifier(String(parts[0])) && validIdentifier(String(parts[1]))
    }

    static func validExecutable(_ value: String) -> Bool {
        guard !value.isEmpty, !value.contains("\0"), !value.contains(" "), value.utf8.count <= 4096 else { return false }
        if value.hasPrefix("/") { return !value.contains("..") && !value.contains(";") && !value.contains("|") }
        return value.range(of: #"\A[A-Za-z0-9][A-Za-z0-9._-]*\z"#, options: .regularExpression) != nil
    }

    static func validPackageName(_ value: String) -> Bool {
        guard !value.isEmpty, value.utf8.count <= 128 else { return false }
        if value.hasPrefix("@") {
            return value.range(of: #"\A@[A-Za-z0-9][A-Za-z0-9._-]*/[A-Za-z0-9][A-Za-z0-9._-]*\z"#, options: .regularExpression) != nil
        }
        return value.range(of: #"\A[A-Za-z0-9][A-Za-z0-9._-]*\z"#, options: .regularExpression) != nil
    }

    static func validWingetName(_ value: String) -> Bool {
        guard !value.isEmpty, value.utf8.count <= 128 else { return false }
        return value.range(of: #"\A[A-Za-z0-9][A-Za-z0-9._+\-]*\z"#, options: .regularExpression) != nil
    }

    static func validWingetExecutable(_ value: String) -> Bool {
        guard !value.isEmpty, value.utf8.count <= 128 else { return false }
        return value.range(of: #"\A[A-Za-z0-9][A-Za-z0-9._\-]*\z"#, options: .regularExpression) != nil
    }

    static func validHttpsURL(_ value: String) -> Bool {
        value.hasPrefix("https://") && !value.contains("\0") && value.utf8.count <= 2048
    }

    static func validRef(_ value: String) -> Bool {
        guard !value.isEmpty, value.utf8.count <= 128 else { return false }
        if value.range(of: #"\A[0-9a-f]{40}\z"#, options: .regularExpression) != nil { return true }
        return value.range(of: #"\A[A-Za-z0-9][A-Za-z0-9._-]*\z"#, options: .regularExpression) != nil
    }

    static func validScriptPath(_ value: String) -> Bool {
        guard !value.isEmpty, !value.hasPrefix("/"), !value.contains("\0"), value.utf8.count <= 4096 else { return false }
        return !value.split(separator: "/", omittingEmptySubsequences: false).contains("..")
    }
}
