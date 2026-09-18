import Foundation

/// Korean particles chosen by the last syllable of the preceding word.
public enum KoreanParticle {
    /// 로/으로: 으로 only after a Korean syllable with a final consonant other
    /// than ㄹ; Latin words and open syllables take 로.
    public static func ro(_ word: String) -> String {
        guard let scalar = word.unicodeScalars.last?.value, (0xAC00...0xD7A3).contains(scalar) else { return "로" }
        let final = (scalar - 0xAC00) % 28
        return final == 0 || final == 8 ? "로" : "으로"
    }
}

/// What choosing a built-in does in the app instead of sending prompt text.
/// The CLIs' own commands (`/plugin`, `/clear`, `/model`…) do not exist in
/// their headless modes, so the app performs the equivalent itself.
public enum SlashCommandAction: Sendable, Equatable {
    case openPlugins, newConversation, showUsage, openSettings, rename, help
    case setModel(String), setPermission(String)
}

/// A built-in whose argument the palette completes after `/name `.
public enum SlashArgument: Sendable, Equatable { case model, permission }

/// Where a command came from, decided where it is discovered. `source` below
/// is Korean badge prose for the Mac's list; only this enum is a vocabulary,
/// so the phone never has to parse a sentence to learn the origin.
public enum SlashCommandOrigin: String, Sendable, Equatable { case app, project, user, plugin }

/// A skill or custom command the composer can complete after a leading `/`.
/// `invocation` is what the CLI expects (`archify`, `sc:analyze`,
/// `oh-my-claudecode:autopilot`); the composer inserts `/<invocation> `.
/// Entries with an `action` run in the app and clear the draft instead.
public struct SlashCommand: Sendable, Equatable, Identifiable {
    public var invocation: String
    public var description: String
    /// Where it came from, for the badge: 사용자 스킬 · 프로젝트 스킬 · 플러그인 <name> · 사용자 명령 · 프로젝트 명령 · Codex 스킬,
    /// or one of `SlashCommandCatalog.appSource` / `modelSource` / `permissionSource` for built-ins and their choices.
    public var source: String
    /// The same fact as `source`, as a value. Set at every discovery site so
    /// no consumer has to recognise the badge prose.
    public var origin: SlashCommandOrigin
    public var action: SlashCommandAction?
    public var argument: SlashArgument?
    public var id: String { invocation }
    public init(invocation: String, description: String, source: String, origin: SlashCommandOrigin, action: SlashCommandAction? = nil, argument: SlashArgument? = nil) {
        self.invocation = invocation; self.description = description; self.source = source; self.origin = origin
        self.action = action; self.argument = argument
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

    /// `"/model cla"` → `("model", "cla")`, `"/model "` → `("model", "")`. Nil
    /// unless exactly one space follows a plain command name.
    public static func argumentQuery(from draft: String) -> (command: String, query: String)? {
        guard draft.hasPrefix("/"), draft.count <= 160 else { return nil }
        let parts = draft.dropFirst().split(separator: " ", maxSplits: 1, omittingEmptySubsequences: false)
        guard parts.count == 2, let command = validName(String(parts[0])) else { return nil }
        let query = String(parts[1])
        guard !query.contains(where: \.isWhitespace) else { return nil }
        return (command, query)
    }

    // MARK: Built-ins

    public static let appSource = "앱 기능"
    public static let modelSource = "모델"
    public static let permissionSource = "작업 권한"

    /// The CLI's own slash commands that the app answers itself, using the
    /// names each CLI's users already know. Gemini has no plugin browser here.
    public static func builtins(provider: String) -> [SlashCommand] {
        func app(_ name: String, _ description: String, action: SlashCommandAction? = nil, argument: SlashArgument? = nil) -> SlashCommand {
            SlashCommand(invocation: name, description: description, source: appSource, origin: .app, action: action, argument: argument)
        }
        let model = app("model", "모델 바꾸기 · 이름을 이어서 고르세요", argument: .model)
        let rename = app("rename", "실행 창 이름 바꾸기", action: .rename)
        let help = app("help", "이 실행 창에서 쓸 수 있는 앱 명령 보기", action: .help)
        switch provider {
        case "claude":
            return [app("plugin", "플러그인 마켓플레이스 열기", action: .openPlugins), model,
                    app("permissions", "작업 권한 바꾸기 · 모드를 이어서 고르세요", argument: .permission),
                    app("clear", "새 대화로 시작 · 다음 입력부터 이전 대화를 잇지 않음", action: .newConversation),
                    app("cost", "이 실행 창의 토큰·비용 보기", action: .showUsage), app("usage", "이 실행 창의 토큰·비용 보기", action: .showUsage),
                    app("config", "MightyClaude 설정 열기", action: .openSettings), rename, help]
        case "codex":
            return [app("plugins", "플러그인 마켓플레이스 열기", action: .openPlugins), model,
                    app("approvals", "작업 권한 바꾸기 · 모드를 이어서 고르세요", argument: .permission),
                    app("new", "새 대화로 시작 · 다음 입력부터 이전 대화를 잇지 않음", action: .newConversation),
                    app("status", "이 실행 창의 토큰·비용 보기", action: .showUsage),
                    app("settings", "MightyClaude 설정 열기", action: .openSettings), rename, help]
        case "gemini":
            return [model, app("approval-mode", "작업 권한 바꾸기 · 모드를 이어서 고르세요", argument: .permission),
                    app("clear", "새 대화로 시작 · 다음 입력부터 이전 대화를 잇지 않음", action: .newConversation),
                    app("stats", "이 실행 창의 토큰·비용 보기", action: .showUsage),
                    app("settings", "MightyClaude 설정 열기", action: .openSettings), rename, help]
        default: return []
        }
    }

    /// The `/help` text: one line per built-in.
    public static func helpText(provider: String) -> String {
        let lines = builtins(provider: provider).map { "/" + $0.invocation + " · " + $0.description }
        return "앱 명령 · " + ProviderOptions.label(provider) + " 실행 창\n" + lines.joined(separator: "\n") + "\n그 밖의 /이름은 스킬·사용자 명령·플러그인 명령으로 CLI에 전달됩니다."
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
            found += skills(in: home.appendingPathComponent(".claude/skills"), source: "사용자 스킬", origin: .user)
            found += commandFiles(in: home.appendingPathComponent(".claude/commands"), source: "사용자 명령", origin: .user)
            found += pluginCommands(home: home)
            if let workspace {
                found += skills(in: workspace.appendingPathComponent(".claude/skills"), source: "프로젝트 스킬", origin: .project)
                found += commandFiles(in: workspace.appendingPathComponent(".claude/commands"), source: "프로젝트 명령", origin: .project)
            }
        case "codex":
            found += skills(in: home.appendingPathComponent(".codex/skills"), source: "Codex 스킬", origin: .user)
            if let workspace { found += skills(in: workspace.appendingPathComponent(".codex/skills"), source: "프로젝트 스킬", origin: .project) }
        default: break
        }
        // Later sources (project) win over earlier ones (user, plugins).
        var byInvocation: [String: SlashCommand] = [:]
        for command in found { byInvocation[command.invocation] = command }
        return Array(byInvocation.values.sorted { $0.invocation.localizedCaseInsensitiveCompare($1.invocation) == .orderedAscending }.prefix(maximumCommands))
    }

    // MARK: Sources

    /// `<dir>/<name>/SKILL.md`; the frontmatter `name` wins over the folder.
    static func skills(in directory: URL, source: String, origin: SlashCommandOrigin, invocationPrefix: String = "") -> [SlashCommand] {
        guard let entries = try? FileManager.default.contentsOfDirectory(at: directory, includingPropertiesForKeys: [.isDirectoryKey], options: [.skipsHiddenFiles]) else { return [] }
        return entries.sorted { $0.lastPathComponent < $1.lastPathComponent }.prefix(maximumCommands).compactMap { folder -> SlashCommand? in
            let file = folder.appendingPathComponent("SKILL.md")
            guard let text = read(file) else { return nil }
            let fields = frontmatter(text)
            let name = fields["name"].flatMap(validName) ?? validName(folder.lastPathComponent)
            guard let name else { return nil }
            return SlashCommand(invocation: invocationPrefix + name, description: fields["description"].map(clean) ?? "", source: source, origin: origin)
        }
    }

    /// `<dir>/<name>.md` and `<dir>/<group>/<name>.md` (invoked as `group:name`).
    static func commandFiles(in directory: URL, source: String, origin: SlashCommandOrigin, invocationPrefix: String = "") -> [SlashCommand] {
        guard let entries = try? FileManager.default.contentsOfDirectory(at: directory, includingPropertiesForKeys: [.isDirectoryKey], options: [.skipsHiddenFiles]) else { return [] }
        var result: [SlashCommand] = []
        for entry in entries.sorted(by: { $0.lastPathComponent < $1.lastPathComponent }).prefix(maximumCommands) {
            if entry.pathExtension == "md", let name = validName(entry.deletingPathExtension().lastPathComponent), let text = read(entry) {
                result.append(SlashCommand(invocation: invocationPrefix + name, description: frontmatter(text)["description"].map(clean) ?? firstLine(text), source: source, origin: origin))
            } else if (try? entry.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) == true, let group = validName(entry.lastPathComponent) {
                result += commandFiles(in: entry, source: source, origin: origin, invocationPrefix: invocationPrefix + group + ":")
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
            result += skills(in: root.appendingPathComponent("skills"), source: source, origin: .plugin, invocationPrefix: plugin + ":")
            result += commandFiles(in: root.appendingPathComponent("commands"), source: source, origin: .plugin, invocationPrefix: plugin + ":")
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
