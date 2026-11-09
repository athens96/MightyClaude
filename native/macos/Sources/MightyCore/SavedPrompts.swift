import Foundation

/// A request the user keeps to send again (저장한 요청). `workspaceId` nil:
/// offered in every workspace; else only in that one.
public struct SavedPrompt: Codable, Sendable, Equatable, Identifiable {
    public var id: String
    /// The palette name: `/prompt:<name>`.
    public var name: String
    public var text: String
    public var workspaceId: String?

    public init(id: String = UUID().uuidString, name: String, text: String, workspaceId: String? = nil) {
        self.id = id; self.name = name; self.text = text; self.workspaceId = workspaceId
    }
}

/// The saved requests: their names, the placeholders they fill when chosen,
/// their palette rows, and the file they live in (`<data>/saved-prompts.json`).
public enum SavedPrompts {
    public static let fileName = "saved-prompts.json"
    public static let invocationPrefix = "prompt:"
    public static let maximumPrompts = 200
    public static let maximumTextBytes = 64 * 1024
    /// The placeholders `expand` fills; any other `{word}` stays as typed.
    public static let placeholders = ["branch", "workspace", "date", "clipboard"]

    /// A name the palette can carry: lower-case letters, digits, `-` and `_`,
    /// spaces turned into `-`, at most 40 characters; nil when nothing is left.
    public static func normalizedName(_ raw: String) -> String? {
        let mapped = raw.lowercased().trimmingCharacters(in: .whitespaces).map { $0 == " " ? "-" : $0 }
        let kept = String(mapped.filter { $0.isLetter || $0.isNumber || $0 == "-" || $0 == "_" }.prefix(40))
        return kept.isEmpty ? nil : kept
    }

    /// The prompts offered in `workspaceId`: its own, then the shared ones, by name; a workspace prompt shadows a shared one of the same name.
    public static func offered(_ prompts: [SavedPrompt], workspaceId: String?) -> [SavedPrompt] {
        let own = prompts.filter { $0.workspaceId != nil && $0.workspaceId == workspaceId }.sorted { $0.name < $1.name }
        let names = Set(own.map(\.name))
        return own + prompts.filter { $0.workspaceId == nil && !names.contains($0.name) }.sorted { $0.name < $1.name }
    }

    /// The palette rows of the offered prompts; choosing one puts its text in the composer.
    public static func commands(_ prompts: [SavedPrompt], workspaceId: String?, source: String) -> [SlashCommand] {
        offered(prompts, workspaceId: workspaceId).map { prompt in
            let summary = prompt.text.split(whereSeparator: \.isNewline).first.map(String.init) ?? ""
            return SlashCommand(invocation: invocationPrefix + prompt.name, description: String(summary.prefix(80)), source: source,
                                origin: prompt.workspaceId == nil ? .user : .project, action: .insertPrompt(prompt.id))
        }
    }

    /// `text` with each known `{placeholder}` replaced by its value; one without a value becomes empty.
    public static func expand(_ text: String, values: [String: String]) -> String {
        var result = text
        for name in placeholders { result = result.replacingOccurrences(of: "{\(name)}", with: values[name] ?? "") }
        return result
    }

    /// The saved prompts in `directory`; none when the file is missing or unreadable.
    public static func load(directory: URL) -> [SavedPrompt] {
        guard let data = try? Data(contentsOf: directory.appendingPathComponent(fileName)),
              let prompts = try? JSONDecoder().decode([SavedPrompt].self, from: data) else { return [] }
        return Array(prompts.filter { normalizedName($0.name) == $0.name && $0.text.utf8.count <= maximumTextBytes }.prefix(maximumPrompts))
    }

    public static func save(_ prompts: [SavedPrompt], directory: URL) throws {
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let encoder = JSONEncoder(); encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try encoder.encode(Array(prompts.prefix(maximumPrompts))).write(to: directory.appendingPathComponent(fileName), options: .atomic)
    }
}
