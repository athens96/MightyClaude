import Foundation

/// A skill or custom command the composer can complete after a leading `/`.
/// `invocation` is what the CLI expects (`archify`, `sc:analyze`,
/// `oh-my-claudecode:autopilot`); the composer inserts `/<invocation> `.
public struct SlashCommand: Sendable, Equatable, Identifiable {
    public var invocation: String
    public var description: String
    /// Where it came from, for the badge: 사용자 스킬 · 프로젝트 스킬 · 플러그인 <name> · 사용자 명령 · 프로젝트 명령 · Codex 스킬.
    public var source: String
    public var id: String { invocation }
    public init(invocation: String, description: String, source: String) {
        self.invocation = invocation; self.description = description; self.source = source
    }
}

/// Scans the same places the CLIs read: user and project skills and commands,
/// installed Claude plugins, Codex skills. Pure file reads, no CLI calls.
public enum SlashCommandCatalog {
    public static let maximumCommands = 400
    static let namePattern = "^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$"

    /// The token being completed: `"/ar"` → `"ar"`, `"/"` → `""`. Nil once a
    /// space follows the command (arguments are being typed) or the draft does
    /// not start with `/`.
    public static func query(from draft: String) -> String? {
        guard draft.hasPrefix("/") else { return nil }
        let rest = draft.dropFirst()
        guard !rest.contains(where: \.isWhitespace) else { return nil }
        guard rest.count <= 80 else { return nil }
        return String(rest)
    }

    /// Prefix matches first (by name), then substring matches of name or description.
    public static func filter(_ commands: [SlashCommand], query: String) -> [SlashCommand] {
        let needle = query.lowercased()
        guard !needle.isEmpty else { return commands }
        let prefix = commands.filter { $0.invocation.lowercased().hasPrefix(needle) }
        let afterColon = commands.filter { command in
            !prefix.contains(command) && command.invocation.split(separator: ":").dropFirst().contains { $0.lowercased().hasPrefix(needle) }
        }
        let contains = commands.filter { command in
            !prefix.contains(command) && !afterColon.contains(command) && (command.invocation.lowercased().contains(needle) || command.description.lowercased().contains(needle))
        }
        return prefix + afterColon + contains
    }

    /// Everything available to `provider` for a pane in `workspacePath`.
    /// Project entries shadow user entries with the same invocation.
    public static func commands(provider: String, workspacePath: String?, home: URL = FileManager.default.homeDirectoryForCurrentUser) -> [SlashCommand] {
        var found: [SlashCommand] = []
        let workspace = workspacePath.map { URL(fileURLWithPath: $0, isDirectory: true) }
        switch provider {
        case "claude":
            found += skills(in: home.appendingPathComponent(".claude/skills"), source: "사용자 스킬")
            found += commandFiles(in: home.appendingPathComponent(".claude/commands"), source: "사용자 명령")
            found += pluginCommands(home: home)
            if let workspace {
                found += skills(in: workspace.appendingPathComponent(".claude/skills"), source: "프로젝트 스킬")
                found += commandFiles(in: workspace.appendingPathComponent(".claude/commands"), source: "프로젝트 명령")
            }
        case "codex":
            found += skills(in: home.appendingPathComponent(".codex/skills"), source: "Codex 스킬")
            if let workspace { found += skills(in: workspace.appendingPathComponent(".codex/skills"), source: "프로젝트 스킬") }
        default: break
        }
        // Later sources (project) win over earlier ones (user, plugins).
        var byInvocation: [String: SlashCommand] = [:]
        for command in found { byInvocation[command.invocation] = command }
        return Array(byInvocation.values.sorted { $0.invocation.localizedCaseInsensitiveCompare($1.invocation) == .orderedAscending }.prefix(maximumCommands))
    }

    // MARK: Sources

    /// `<dir>/<name>/SKILL.md`; the frontmatter `name` wins over the folder.
    static func skills(in directory: URL, source: String, invocationPrefix: String = "") -> [SlashCommand] {
        guard let entries = try? FileManager.default.contentsOfDirectory(at: directory, includingPropertiesForKeys: [.isDirectoryKey], options: [.skipsHiddenFiles]) else { return [] }
        return entries.sorted { $0.lastPathComponent < $1.lastPathComponent }.prefix(maximumCommands).compactMap { folder -> SlashCommand? in
            let file = folder.appendingPathComponent("SKILL.md")
            guard let text = read(file) else { return nil }
            let fields = frontmatter(text)
            let name = fields["name"].flatMap(validName) ?? validName(folder.lastPathComponent)
            guard let name else { return nil }
            return SlashCommand(invocation: invocationPrefix + name, description: fields["description"].map(clean) ?? "", source: source)
        }
    }

    /// `<dir>/<name>.md` and `<dir>/<group>/<name>.md` (invoked as `group:name`).
    static func commandFiles(in directory: URL, source: String, invocationPrefix: String = "") -> [SlashCommand] {
        guard let entries = try? FileManager.default.contentsOfDirectory(at: directory, includingPropertiesForKeys: [.isDirectoryKey], options: [.skipsHiddenFiles]) else { return [] }
        var result: [SlashCommand] = []
        for entry in entries.sorted(by: { $0.lastPathComponent < $1.lastPathComponent }).prefix(maximumCommands) {
            if entry.pathExtension == "md", let name = validName(entry.deletingPathExtension().lastPathComponent), let text = read(entry) {
                result.append(SlashCommand(invocation: invocationPrefix + name, description: frontmatter(text)["description"].map(clean) ?? firstLine(text), source: source))
            } else if (try? entry.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) == true, let group = validName(entry.lastPathComponent) {
                result += commandFiles(in: entry, source: source, invocationPrefix: invocationPrefix + group + ":")
            }
        }
        return result
    }

    /// Installed Claude plugins (`installed_plugins.json`): each plugin's
    /// `skills/` and `commands/` are invoked as `<plugin>:<name>`.
    static func pluginCommands(home: URL) -> [SlashCommand] {
        let registry = home.appendingPathComponent(".claude/plugins/installed_plugins.json")
        guard let data = try? Data(contentsOf: registry), let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let plugins = object["plugins"] as? [String: Any] else { return [] }
        var result: [SlashCommand] = []
        for (key, value) in plugins.sorted(by: { $0.key < $1.key }) {
            guard let plugin = validName(String(key.split(separator: "@").first ?? "")), let installs = value as? [[String: Any]],
                  let path = installs.compactMap({ $0["installPath"] as? String }).first, path.hasPrefix("/") else { continue }
            let root = URL(fileURLWithPath: path, isDirectory: true)
            let source = "플러그인 " + plugin
            result += skills(in: root.appendingPathComponent("skills"), source: source, invocationPrefix: plugin + ":")
            result += commandFiles(in: root.appendingPathComponent("commands"), source: source, invocationPrefix: plugin + ":")
        }
        return result
    }

    // MARK: Parsing

    static func read(_ url: URL) -> String? {
        guard let attributes = try? FileManager.default.attributesOfItem(atPath: url.path), (attributes[.size] as? Int ?? 0) <= 512 * 1024 else { return nil }
        return try? String(contentsOf: url, encoding: .utf8)
    }
    static func validName(_ value: String) -> String? { value.range(of: namePattern, options: .regularExpression) != nil ? value : nil }

    /// `---\nkey: value\n---` at the top of the file. Values may be quoted;
    /// multi-line values keep their first line.
    public static func frontmatter(_ text: String) -> [String: String] {
        var lines = text.split(separator: "\n", omittingEmptySubsequences: false).map(String.init)
        guard lines.first?.trimmingCharacters(in: .whitespaces) == "---" else { return [:] }
        lines.removeFirst()
        var fields: [String: String] = [:]
        for line in lines.prefix(200) {
            if line.trimmingCharacters(in: .whitespaces) == "---" { break }
            guard let colon = line.firstIndex(of: ":"), !line.hasPrefix(" "), !line.hasPrefix("\t") else { continue }
            let key = line[..<colon].trimmingCharacters(in: .whitespaces)
            var value = line[line.index(after: colon)...].trimmingCharacters(in: .whitespaces)
            if value.count >= 2, let first = value.first, let last = value.last, first == last, first == "\"" || first == "'" { value = String(value.dropFirst().dropLast()) }
            if !key.isEmpty, !value.isEmpty, !value.hasPrefix(">") , !value.hasPrefix("|") { fields[key] = value }
        }
        return fields
    }
    static func clean(_ value: String) -> String { String(value.replacingOccurrences(of: "\n", with: " ").trimmingCharacters(in: .whitespacesAndNewlines).prefix(240)) }
    static func firstLine(_ text: String) -> String {
        let body = frontmatter(text).isEmpty ? text : text.components(separatedBy: "\n---").dropFirst().joined(separator: "\n---")
        return clean(body.split(separator: "\n").map(String.init).first(where: { !$0.trimmingCharacters(in: .whitespaces).isEmpty && !$0.hasPrefix("#") }) ?? "")
    }
}
