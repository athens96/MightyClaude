import Foundation

/// 에이전트 맥락: what each CLI reads about a workspace before it starts —
/// its instruction file (Claude `CLAUDE.md`, Codex `AGENTS.md`, Antigravity
/// `GEMINI.md`), the MCP servers it is given and the Claude hooks that run.
/// The instruction files can be edited and made the same; the server and
/// hook lists are read only (they live in the CLIs' own settings).
public enum AgentContext {
    public struct InstructionFile: Sendable, Equatable, Identifiable {
        public var id: String { name }
        public let provider: String
        public let name: String
        /// nil: the file does not exist.
        public let text: String?
    }

    public struct MCPServer: Sendable, Equatable, Identifiable {
        public var id: String { provider + "|" + scope + "|" + name }
        public let provider: String
        public let name: String
        /// `project` (the workspace's own files) or `user`.
        public let scope: String
        /// The command or address it starts or connects to, for recognising it.
        public let target: String
        public let source: String
    }

    public struct Hook: Sendable, Equatable, Identifiable {
        public var id: String { scope + "|" + event }
        public let event: String
        public let commands: [String]
        public let scope: String
        public let source: String
    }

    /// Instruction files by provider, in the order the app lists agents.
    public static let instructionNames: [(provider: String, name: String)] = [("claude", "CLAUDE.md"), ("codex", "AGENTS.md"), ("gemini", "GEMINI.md")]
    public static let maximumFileBytes = 512 * 1024

    public static func instructionFiles(root: URL) -> [InstructionFile] {
        instructionNames.map { entry in
            let url = root.appendingPathComponent(entry.name)
            let text = (try? Data(contentsOf: url)).flatMap { $0.count <= maximumFileBytes ? String(data: $0, encoding: .utf8) : nil }
            return InstructionFile(provider: entry.provider, name: entry.name, text: text)
        }
    }

    /// Whether the files that exist hold different text (blank lines at the end aside).
    public static func diverged(_ files: [InstructionFile]) -> Bool {
        Set(files.compactMap { $0.text?.trimmingCharacters(in: .whitespacesAndNewlines) }).count > 1
    }

    /// Writes `text` as the instruction file `name` (one of `instructionNames`) in `root`.
    public static func write(_ text: String, name: String, root: URL) throws {
        guard instructionNames.contains(where: { $0.name == name }), text.utf8.count <= maximumFileBytes else { throw CocoaError(.fileWriteInvalidFileName) }
        try Data(text.utf8).write(to: root.appendingPathComponent(name), options: .atomic)
    }

    // MARK: - MCP servers

    private static func json(_ url: URL) -> [String: Any]? {
        (try? Data(contentsOf: url)).flatMap { try? JSONSerialization.jsonObject(with: $0) as? [String: Any] }
    }

    /// The `mcpServers` object of a Claude or Gemini settings file.
    static func servers(_ object: [String: Any]?, provider: String, scope: String, source: String) -> [MCPServer] {
        guard let servers = object?["mcpServers"] as? [String: Any] else { return [] }
        return servers.keys.sorted().map { name in
            let entry = servers[name] as? [String: Any] ?? [:]
            let command = ([entry["command"] as? String] + ((entry["args"] as? [Any]) ?? []).map { $0 as? String }).compactMap { $0 }.joined(separator: " ")
            let target = (entry["url"] as? String) ?? (entry["httpUrl"] as? String) ?? command
            return MCPServer(provider: provider, name: name, scope: scope, target: target, source: source)
        }
    }

    /// Codex's `[mcp_servers.<name>]` tables in `config.toml` (their `command`, `args` and `url` lines).
    public static func codexServers(toml: String, source: String) -> [MCPServer] {
        var result: [MCPServer] = []
        var name: String?, command = "", args = "", url = ""
        func flush() {
            if let name { result.append(MCPServer(provider: "codex", name: name, scope: "user", target: url.isEmpty ? [command, args].filter { !$0.isEmpty }.joined(separator: " ") : url, source: source)) }
        }
        func value(_ line: String) -> String {
            let raw = line.split(separator: "=", maxSplits: 1).last.map { $0.trimmingCharacters(in: .whitespaces) } ?? ""
            if raw.hasPrefix("[") {
                return raw.trimmingCharacters(in: CharacterSet(charactersIn: "[]")).split(separator: ",")
                    .map { $0.trimmingCharacters(in: .whitespaces).trimmingCharacters(in: CharacterSet(charactersIn: "\"'")) }.joined(separator: " ")
            }
            return raw.trimmingCharacters(in: CharacterSet(charactersIn: "\"'"))
        }
        for rawLine in toml.split(whereSeparator: \.isNewline) {
            let line = rawLine.trimmingCharacters(in: .whitespaces)
            if line.hasPrefix("[") {
                // `[mcp_servers.name]` opens a server; `[mcp_servers.name.env]` stays inside it.
                if line.hasPrefix("[mcp_servers.") {
                    let inner = line.dropFirst("[mcp_servers.".count).prefix { $0 != "]" }
                    let first = String(inner.split(separator: ".").first ?? "").trimmingCharacters(in: CharacterSet(charactersIn: "\"'"))
                    if first != name { flush(); name = first; command = ""; args = ""; url = "" }
                } else { flush(); name = nil }
                continue
            }
            guard name != nil else { continue }
            if line.hasPrefix("command") { command = value(line) }
            else if line.hasPrefix("args") { args = value(line) }
            else if line.hasPrefix("url") { url = value(line) }
        }
        flush()
        return result
    }

    /// Every MCP server the three CLIs give a run in `root`: project files first, then the user's.
    public static func mcpServers(root: URL, home: URL = FileManager.default.homeDirectoryForCurrentUser) -> [MCPServer] {
        var result = servers(json(root.appendingPathComponent(".mcp.json")), provider: "claude", scope: "project", source: ".mcp.json")
        let claudeUser = json(home.appendingPathComponent(".claude.json"))
        let projects = claudeUser?["projects"] as? [String: Any]
        result += servers(projects?[root.path] as? [String: Any], provider: "claude", scope: "project", source: "~/.claude.json")
        result += servers(claudeUser, provider: "claude", scope: "user", source: "~/.claude.json")
        if let toml = try? String(contentsOf: home.appendingPathComponent(".codex/config.toml"), encoding: .utf8) {
            result += codexServers(toml: toml, source: "~/.codex/config.toml")
        }
        result += servers(json(root.appendingPathComponent(".gemini/settings.json")), provider: "gemini", scope: "project", source: ".gemini/settings.json")
        result += servers(json(home.appendingPathComponent(".gemini/settings.json")), provider: "gemini", scope: "user", source: "~/.gemini/settings.json")
        return result
    }

    // MARK: - Hooks

    /// The `hooks` of a Claude settings file: one row per event with the commands it runs.
    static func hooks(_ object: [String: Any]?, scope: String, source: String) -> [Hook] {
        guard let hooks = object?["hooks"] as? [String: Any] else { return [] }
        return hooks.keys.sorted().compactMap { event in
            let groups = hooks[event] as? [[String: Any]] ?? []
            let commands = groups.flatMap { ($0["hooks"] as? [[String: Any]] ?? []).compactMap { $0["command"] as? String } }
            return commands.isEmpty ? nil : Hook(event: event, commands: commands, scope: scope, source: source)
        }
    }

    public static func claudeHooks(root: URL, home: URL = FileManager.default.homeDirectoryForCurrentUser) -> [Hook] {
        hooks(json(root.appendingPathComponent(".claude/settings.local.json")), scope: "local", source: ".claude/settings.local.json")
            + hooks(json(root.appendingPathComponent(".claude/settings.json")), scope: "project", source: ".claude/settings.json")
            + hooks(json(home.appendingPathComponent(".claude/settings.json")), scope: "user", source: "~/.claude/settings.json")
    }
}
