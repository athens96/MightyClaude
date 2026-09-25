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
        let registry = home.appendingPathComponent(".claude/plugins/installed_plugins.json")
        guard let data = CLIAccountSupport.boundedData(registry, maximumBytes: 4 * 1024 * 1024),
              let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let plugins = object["plugins"] as? [String: Any],
              let records = plugins[pluginID] as? [[String: Any]] else { return .missing }
        return records.contains { $0["scope"] as? String == "user" } ? .installed : .missing
    }

    /// mcp: name present under top-level mcpServers of ~/.claude.json.
    private static func probeMcp(name: String, home: URL) -> Result {
        let claudeJson = home.appendingPathComponent(".claude.json")
        guard let data = CLIAccountSupport.boundedData(claudeJson, maximumBytes: 4 * 1024 * 1024),
              let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let mcpServers = object["mcpServers"] as? [String: Any] else { return .missing }
        return mcpServers[name] != nil ? .installed : .missing
    }

    /// skill: ~/.claude/skills/<derived>/SKILL.md exists.
    /// derived = last URL path component with trailing .git stripped.
    private static func probeSkill(url: String, home: URL) -> Result {
        var component = URL(string: url)?.lastPathComponent ?? ""
        if component.hasSuffix(".git") { component = String(component.dropLast(4)) }
        guard !component.isEmpty else { return .missing }
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
