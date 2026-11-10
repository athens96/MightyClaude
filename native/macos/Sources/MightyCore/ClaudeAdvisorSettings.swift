import Foundation

/// Whether Claude's own settings turn the advisor on. The advisor is Claude Code's experimental server-side advisor
/// tool: `claude --advisor <model>`, the `advisorModel` setting, or `/advisor [model|off]`, and the
/// `CLAUDE_CODE_DISABLE_ADVISOR_TOOL` variable turns it off. A pane left on "follow Claude's settings" passes nothing,
/// so what the run will do is in Claude's settings files; they are read here, never written.
///
/// The files, in the order Claude reads them, a later one winning: the user's `settings.json` in Claude's config
/// folder, then the working folder's `.claude/settings.json` and `.claude/settings.local.json`. A file is read only
/// up to `maximumBytes`; one that is missing, larger, or not a JSON object says nothing. A file is read again only
/// when its size or modification time changed.
public enum ClaudeAdvisorSettings {
    public static let maximumBytes = 1024 * 1024
    /// The variable that turns the advisor off, in the run's environment or in a settings file's `env`.
    public static let disableVariable = "CLAUDE_CODE_DISABLE_ADVISOR_TOOL"

    /// What one settings file says about the advisor.
    public struct Entry: Equatable, Sendable {
        /// `advisorModel`: a model's name, or `off` when the key says off (`"off"`, empty, false or null); nil when the
        /// file has no such key.
        public var model: Choice?
        /// The file's `env` sets the disable variable: true when it turns the advisor off, false when it sets it to
        /// something that does not, nil when it does not name it.
        public var disabled: Bool?
        public init(model: Choice? = nil, disabled: Bool? = nil) { self.model = model; self.disabled = disabled }
    }
    public enum Choice: Equatable, Sendable { case off, model(String) }

    /// The settings files in the order Claude reads them. `workspace` is the run's working folder (nil reads the
    /// user's file alone: another computer's folder is not on this Mac).
    public static func files(workspace: URL?, configDirectory: URL) -> [URL] {
        var files = [configDirectory.appendingPathComponent("settings.json")]
        if let workspace {
            files.append(workspace.appendingPathComponent(".claude/settings.json"))
            files.append(workspace.appendingPathComponent(".claude/settings.local.json"))
        }
        return files
    }

    /// The advisor's model Claude's settings name for a run in `workspace`, nil when none does or when they turn it off.
    public static func model(workspace: URL?, configDirectory: URL, cache: Cache = .shared) -> String? {
        resolve(files(workspace: workspace, configDirectory: configDirectory).map { cache.entry($0) })
    }

    /// The files' entries, in order, as one answer: the last file that names `advisorModel` decides the model, and the
    /// last that sets the disable variable decides whether it is off.
    static func resolve(_ entries: [Entry]) -> String? {
        var choice: Choice?, disabled = false
        for entry in entries {
            if let model = entry.model { choice = model }
            if let value = entry.disabled { disabled = value }
        }
        guard !disabled, case .model(let name)? = choice else { return nil }
        return name
    }

    /// What a settings file's bytes say. Anything that is not a JSON object says nothing.
    static func entry(_ data: Data) -> Entry {
        guard let object = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] else { return Entry() }
        var entry = Entry()
        if let value = object["advisorModel"] {
            if let name = value as? String, !name.isEmpty, name.lowercased() != "off", CoreValidation.model(name) { entry.model = .model(name) }
            else { entry.model = .off }
        }
        if let value = (object["env"] as? [String: Any])?[disableVariable] { entry.disabled = truthy(value) }
        return entry
    }

    /// A variable's value as Claude Code reads a switch: 1, true, yes or on.
    public static func truthy(_ value: Any?) -> Bool {
        if let flag = value as? Bool { return flag }
        if let number = value as? Int { return number != 0 }
        guard let text = value as? String else { return false }
        return ["1", "true", "yes", "on"].contains(text.trimmingCharacters(in: .whitespacesAndNewlines).lowercased())
    }

    /// The advisor's model a request starts with, when one is known to be on: the pane's own choice
    /// (`RunSettings.advisor`: a model, or `RunSettings.advisorOff`), else what Claude's settings name. Nil when the
    /// pane turned it off, when the run's environment does (`environmentDisables`, which no model choice overrides),
    /// or when nothing names one.
    public static func configured(pane: String?, environmentDisables: Bool = false, settings: () -> String?) -> String? {
        if pane == RunSettings.advisorOff || environmentDisables { return nil }
        if let pane { return CoreValidation.model(pane) ? pane : nil }
        return settings()
    }

    public final class Cache: @unchecked Sendable {
        public static let shared = Cache()
        private let lock = NSLock()
        private var files: [String: (stamp: String, entry: Entry)] = [:]
        public init() {}

        func entry(_ file: URL) -> Entry {
            guard let attributes = try? FileManager.default.attributesOfItem(atPath: file.path), attributes[.type] as? FileAttributeType == .typeRegular,
                  let size = attributes[.size] as? Int, size > 0, size <= ClaudeAdvisorSettings.maximumBytes else {
                lock.lock(); files.removeValue(forKey: file.path); lock.unlock()
                return Entry()
            }
            let stamp = "\(size)|\((attributes[.modificationDate] as? Date)?.timeIntervalSince1970 ?? 0)"
            lock.lock()
            if let held = files[file.path], held.stamp == stamp { lock.unlock(); return held.entry }
            lock.unlock()
            var entry = Entry()
            if let handle = try? FileHandle(forReadingFrom: file) {
                defer { try? handle.close() }
                if let data = try? handle.read(upToCount: ClaudeAdvisorSettings.maximumBytes + 1), data.count <= ClaudeAdvisorSettings.maximumBytes {
                    entry = ClaudeAdvisorSettings.entry(data)
                }
            }
            lock.lock()
            if files.count >= 256 { files.removeAll() }
            files[file.path] = (stamp, entry)
            lock.unlock()
            return entry
        }
    }
}
