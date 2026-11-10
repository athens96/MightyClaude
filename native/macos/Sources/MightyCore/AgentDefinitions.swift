import Foundation

/// A custom sub-agent as its definition file's frontmatter says it: only what the flow diagram draws of it.
public struct AgentDefinition: Equatable, Sendable {
    /// The name an Agent call's `subagent_type` uses: the frontmatter's `name`, else the file's own.
    public var name: String
    /// One of Claude Code's agent colours (`FlowRoles.colors`); nil when the file names none, or one Claude Code lacks.
    public var color: String?
    /// The model the definition asks for (an alias, a model id or `inherit`); nil when it names none.
    public var model: String?
    public init(name: String, color: String? = nil, model: String? = nil) { self.name = name; self.color = color; self.model = model }
}

/// Reads custom sub-agents' colour and model from their definition files, for the flow diagram's headers
/// (`FlowRoles.subagent`): the project's `<workspace>/.claude/agents/*.md`, then the user's
/// `<Claude config>/agents/*.md` (a project agent wins over a user agent of the same name), and a plugin's agent
/// (`plugin:agent`) from that plugin's own `agents` folder. Nothing is written; a file's head is read up to
/// `maximumBytes`, a folder up to `maximumFiles` files, and a file that does not open or has no frontmatter is left out.
public enum AgentDefinitions {
    public static let maximumBytes = 16 * 1024
    public static let maximumFiles = 256
    static let maximumLines = 80
    static let maximumPluginIndexBytes = 1024 * 1024

    /// The definition a file's text holds, or nil when it does not open with a frontmatter block. The frontmatter is read
    /// line by line, not as YAML: a top-level `key: value`, with or without quotes and with a trailing `# comment` cut;
    /// lists, blocks and anything indented are passed over, so a file YAML would reject still gives what it can.
    public static func parse(_ text: String, fileName: String) -> AgentDefinition? {
        var lines = text.split(omittingEmptySubsequences: false, whereSeparator: \.isNewline).makeIterator()
        var first = lines.next()
        if let head = first, head.hasPrefix("\u{FEFF}") { first = head.dropFirst() }
        guard let first, first.trimmingCharacters(in: .whitespaces) == "---" else { return nil }
        var fields: [String: String] = [:]
        var closed = false
        var count = 0
        while let line = lines.next(), count < maximumLines {
            count += 1
            let trimmed = line.trimmingCharacters(in: .whitespaces)
            if trimmed == "---" || trimmed == "..." { closed = true; break }
            guard let first = line.first, first.isLetter || first == "_", let colon = line.firstIndex(of: ":") else { continue }
            let key = line[..<colon].trimmingCharacters(in: .whitespaces).lowercased()
            guard ["name", "color", "model"].contains(key), fields[key] == nil else { continue }
            if let value = scalar(String(line[line.index(after: colon)...])) { fields[key] = value }
        }
        guard closed else { return nil }
        let fallback = (fileName as NSString).deletingPathExtension
        let name = ActivitySupport.clean(fields["name"] ?? fallback, maximumBytes: 160, singleLine: true)
        guard !name.isEmpty else { return nil }
        let color = fields["color"]?.lowercased()
        let model = fields["model"]
        return AgentDefinition(name: name, color: color.flatMap { FlowRoles.colors.contains($0) ? $0 : nil },
                               model: model.flatMap { CoreValidation.model($0) ? $0 : nil })
    }

    /// A frontmatter value on its key's line: quotes taken off, a trailing comment cut; nil for an empty value and for
    /// the start of a list, a mapping or a block.
    static func scalar(_ raw: String) -> String? {
        var value = raw.trimmingCharacters(in: .whitespaces)
        guard let first = value.first, !"[{|>&*!".contains(first) else { return nil }
        if first == "\"" || first == "'" {
            guard let end = value.dropFirst().firstIndex(of: first) else { return nil }
            value = String(value[value.index(after: value.startIndex)..<end])
        } else if let comment = value.range(of: " #") {
            value = String(value[..<comment.lowerBound]).trimmingCharacters(in: .whitespaces)
        }
        return value.isEmpty ? nil : value
    }

    /// Claude Code's own folder: `$CLAUDE_CONFIG_DIR`, else `~/.claude`.
    public static func configDirectory(home: URL = FileManager.default.homeDirectoryForCurrentUser,
                                       environment: [String: String] = ProcessInfo.processInfo.environment) -> URL {
        environment["CLAUDE_CONFIG_DIR"].flatMap { $0.isEmpty ? nil : URL(fileURLWithPath: $0, isDirectory: true) }
            ?? home.appendingPathComponent(".claude", isDirectory: true)
    }

    /// The definitions of the agents `types` name, by type: project and user agents by their name, a plugin's agent by
    /// `plugin:agent`. `workspace` is the pane's local folder (nil reads no project agents). Folders are read again
    /// only when a file in them came, went or changed (`cache`).
    public static func load(types: Set<String>, workspace: URL?, configDirectory: URL, cache: Cache = .shared) -> [String: AgentDefinition] {
        guard !types.isEmpty else { return [:] }
        var found: [String: AgentDefinition] = [:]
        // The user's first, so the project's replace them.
        var folders = [configDirectory.appendingPathComponent("agents", isDirectory: true)]
        if let workspace { folders.append(workspace.appendingPathComponent(".claude/agents", isDirectory: true)) }
        for folder in folders {
            for definition in cache.definitions(in: folder) where types.contains(definition.name) { found[definition.name] = definition }
        }
        let scoped = types.filter { $0.contains(":") && found[$0] == nil }
        if !scoped.isEmpty {
            let installs = cache.pluginFolders(configDirectory: configDirectory)
            for type in scoped {
                let parts = type.split(separator: ":", omittingEmptySubsequences: false)
                guard parts.count >= 2, let plugin = parts.first, let agent = parts.last, !agent.isEmpty,
                      !agent.contains("/"), agent != "..", let folder = installs[String(plugin)] else { continue }
                let folderDefinitions = cache.definitions(in: folder.appendingPathComponent("agents", isDirectory: true))
                // The file is named after the agent; its frontmatter's name is the same one without the plugin.
                if var definition = folderDefinitions.first(where: { $0.name == agent }) {
                    definition.name = type
                    found[type] = definition
                }
            }
        }
        return found
    }

    /// What was read of each folder, kept until the folder's files change.
    public final class Cache: @unchecked Sendable {
        public static let shared = Cache()
        private let lock = NSLock()
        private var folders: [String: (stamp: String, definitions: [AgentDefinition])] = [:]
        private var plugins: [String: (stamp: String, folders: [String: URL])] = [:]
        public init() {}

        /// Every definition in `folder`'s `*.md` files (not its sub-folders), in file-name order.
        func definitions(in folder: URL) -> [AgentDefinition] {
            let manager = FileManager.default
            guard let names = try? manager.contentsOfDirectory(atPath: folder.path) else {
                lock.lock(); folders.removeValue(forKey: folder.path); lock.unlock()
                return []
            }
            let files = names.filter { $0.lowercased().hasSuffix(".md") && !$0.hasPrefix(".") }.sorted().prefix(AgentDefinitions.maximumFiles)
            var stamp = ""
            var readable: [(name: String, url: URL)] = []
            for name in files {
                let url = folder.appendingPathComponent(name)
                guard let attributes = try? manager.attributesOfItem(atPath: url.path), attributes[.type] as? FileAttributeType == .typeRegular else { continue }
                let modified = (attributes[.modificationDate] as? Date)?.timeIntervalSince1970 ?? 0
                stamp += "\(name)|\(attributes[.size] as? Int ?? 0)|\(modified)\n"
                readable.append((name, url))
            }
            lock.lock()
            if let held = folders[folder.path], held.stamp == stamp { lock.unlock(); return held.definitions }
            lock.unlock()
            let definitions = readable.compactMap { file -> AgentDefinition? in
                guard let text = head(of: file.url) else { return nil }
                return AgentDefinitions.parse(text, fileName: file.name)
            }
            lock.lock(); folders[folder.path] = (stamp, definitions); lock.unlock()
            return definitions
        }

        /// Each installed plugin's folder by the plugin's name, from Claude Code's own list
        /// (`plugins/installed_plugins.json`: `"<plugin>@<marketplace>": [{ "installPath": … }]`).
        func pluginFolders(configDirectory: URL) -> [String: URL] {
            let index = configDirectory.appendingPathComponent("plugins/installed_plugins.json")
            guard let attributes = try? FileManager.default.attributesOfItem(atPath: index.path),
                  let size = attributes[.size] as? Int, size <= AgentDefinitions.maximumPluginIndexBytes else { return [:] }
            let stamp = "\(size)|\((attributes[.modificationDate] as? Date)?.timeIntervalSince1970 ?? 0)"
            lock.lock()
            if let held = plugins[index.path], held.stamp == stamp { lock.unlock(); return held.folders }
            lock.unlock()
            var result: [String: URL] = [:]
            if let data = try? Data(contentsOf: index), let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
               let installed = object["plugins"] as? [String: Any] {
                for (key, value) in installed.sorted(by: { $0.key < $1.key }) {
                    let plugin = String(key.prefix { $0 != "@" })
                    guard !plugin.isEmpty, result[plugin] == nil,
                          let path = ((value as? [[String: Any]])?.first ?? (value as? [String: Any]))?["installPath"] as? String, !path.isEmpty else { continue }
                    result[plugin] = URL(fileURLWithPath: path, isDirectory: true)
                }
            }
            lock.lock(); plugins[index.path] = (stamp, result); lock.unlock()
            return result
        }

        /// The first `maximumBytes` of a file, which hold any frontmatter worth reading.
        private func head(of url: URL) -> String? {
            guard let handle = try? FileHandle(forReadingFrom: url) else { return nil }
            defer { try? handle.close() }
            guard let data = try? handle.read(upToCount: AgentDefinitions.maximumBytes), !data.isEmpty else { return nil }
            return String(decoding: data, as: UTF8.self)
        }
    }
}
