import Foundation

/// Injected paths for ToolkitProbe so tests use temporary directories.
public struct ToolkitProbeContext: Sendable {
    public let home: URL
    public let environment: [String: String]
    public let appDataDir: URL
    /// Ordered list of brew installation prefixes (/opt/homebrew, /usr/local).
    /// Overrideable so tests never touch real system paths.
    public let brewPrefixes: [String]

    public init(home: URL, environment: [String: String] = [:], appDataDir: URL,
                brewPrefixes: [String] = ["/opt/homebrew", "/usr/local"]) {
        self.home = home
        self.environment = environment
        self.appDataDir = appDataDir
        self.brewPrefixes = brewPrefixes
    }
}

/// Machine-wide, presence-only detection for toolkit entries.
/// Files and paths only — no process launch, no handshake.
public enum ToolkitProbe {

    public enum Result: Sendable, Equatable { case installed, missing }

    public static func probe(entry: ToolkitEntry, approval: ToolkitApproval?, context: ToolkitProbeContext) -> Result {
        switch entry.install {
        case .plugin(_, let pluginID):
            return probePlugin(pluginID: pluginID, home: context.home)
        case .mcp(let name, _, _):
            return probeMcp(name: name, home: context.home)
        case .skill(let url):
            return probeSkill(url: url, home: context.home)
        case .package(let manager, let name, _):
            return probePackage(manager: manager, name: name, environment: context.environment,
                                brewPrefixes: context.brewPrefixes)
        case .repoScript:
            return probeRepoScript(approval: approval, appDataDir: context.appDataDir)
        }
    }

    /// Path the install runner writes after a repoScript exits 0.
    public static func repoScriptMarker(appDataDir: URL, resolvedCommit: String) -> URL {
        appDataDir.appendingPathComponent("toolkit-clones/\(resolvedCommit)/.toolkit-script-complete")
    }

    // MARK: - Per-template probes

    /// plugin: exact pluginID key in installed_plugins.json with at least one
    /// record whose scope is "user".  A project- or local-scope record alone
    /// does not count.
    private static func probePlugin(pluginID: String, home: URL) -> Result {
        userScopePluginInstalled(home: home, matching: { $0 == pluginID }) ? .installed : .missing
    }

    /// Whether installed_plugins.json has a plugin whose ID `matching` accepts
    /// with at least one user-scope record. The setup screen asks this too.
    public static func userScopePluginInstalled(home: URL, matching: (String) -> Bool) -> Bool {
        let registry = home.appendingPathComponent(".claude/plugins/installed_plugins.json")
        guard let data = CLIAccountSupport.boundedData(registry, maximumBytes: 4 * 1024 * 1024),
              let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let plugins = object["plugins"] as? [String: Any] else { return false }
        return plugins.contains { id, value in
            matching(id) && ((value as? [[String: Any]])?.contains { $0["scope"] as? String == "user" } ?? false)
        }
    }

    /// mcp: name present under top-level mcpServers of ~/.claude.json.
    private static func probeMcp(name: String, home: URL) -> Result {
        let claudeJson = home.appendingPathComponent(".claude.json")
        guard let data = CLIAccountSupport.boundedData(claudeJson, maximumBytes: 4 * 1024 * 1024),
              let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let mcpServers = object["mcpServers"] as? [String: Any] else { return .missing }
        return mcpServers[name] != nil ? .installed : .missing
    }

    /// The skill's folder name under ~/.claude/skills: the URL's last path component with ".git" (any case) taken off.
    /// Nil when the address names no component. Throws, with the localized why, when the address is no URL or the component is no plain
    /// folder name: "." or "..", a drive (`D:evil`), a separator, or a character Windows refuses would join to a folder outside
    /// ~/.claude/skills (the same rule as Windows' ToolkitProbe.SkillFolder and the Rust app's skill_folder).
    static func skillFolder(url: String) throws -> String? {
        guard let parsed = URL(string: url), parsed.scheme != nil else {
            throw ToolkitStoreError(L("toolkit.error.skillAddress", ["url": url]))
        }
        var component = parsed.lastPathComponent
        if component.lowercased().hasSuffix(".git") { component = String(component.dropLast(4)) }
        if component.isEmpty || component == "/" { return nil }
        let refused = CharacterSet(charactersIn: ":\\/<>\"|?*").union(.controlCharacters)
        if component == "." || component == ".." || component.unicodeScalars.contains(where: { refused.contains($0) }) {
            throw ToolkitStoreError(L("toolkit.error.skillFolder", ["name": component, "url": url]))
        }
        return component
    }

    /// skill: ~/.claude/skills/<derived>/SKILL.md exists.
    private static func probeSkill(url: String, home: URL) -> Result {
        guard let component = (try? skillFolder(url: url)) ?? nil else { return .missing }
        let skillMd = home.appendingPathComponent(".claude/skills/\(component)/SKILL.md")
        return FileManager.default.fileExists(atPath: skillMd.path) ? .installed : .missing
    }

    /// package: brew = <prefix>/opt/<name> for /opt/homebrew or /usr/local;
    /// npm = <npmBinDir>/../lib/node_modules/<name>.
    private static func probePackage(manager: ToolkitPackageManager, name: String,
                                     environment: [String: String], brewPrefixes: [String]) -> Result {
        switch manager {
        case .brew:
            for prefix in brewPrefixes where FileManager.default.fileExists(atPath: "\(prefix)/opt/\(name)") {
                return .installed
            }
            return .missing
        case .npm:
            let paths = (environment["PATH"] ?? "").split(separator: ":").map(String.init)
            for dir in paths where FileManager.default.isExecutableFile(atPath: dir + "/npm") {
                let moduleDir = URL(fileURLWithPath: dir).deletingLastPathComponent()
                    .appendingPathComponent("lib/node_modules/\(name)")
                if FileManager.default.fileExists(atPath: moduleDir.path) { return .installed }
            }
            return .missing
        case .winget:
            return .missing
        }
    }

    /// repoScript: the completion marker for the approved commit SHA exists.
    private static func probeRepoScript(approval: ToolkitApproval?, appDataDir: URL) -> Result {
        guard let sha = approval?.resolvedCommit else { return .missing }
        return FileManager.default.fileExists(atPath: repoScriptMarker(appDataDir: appDataDir, resolvedCommit: sha).path)
            ? .installed : .missing
    }
}
