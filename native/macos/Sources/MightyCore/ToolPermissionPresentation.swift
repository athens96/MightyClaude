import Foundation

/// One labelled value of a tool request, shown instead of raw JSON.
public struct ToolPermissionField: Equatable, Sendable {
    public let label: String
    public let value: String
    /// Monospaced box (commands, paths, code); otherwise running text.
    public let code: Bool
    public init(label: String, value: String, code: Bool = false) { self.label = label; self.value = value; self.code = code }
}

/// A readable form of a `can_use_tool` request. It only rearranges the exact
/// input for display; approval still sends the original, unmodified input.
public struct ToolPermissionPresentation: Equatable, Sendable {
    /// What the tool does, e.g. "Run a command" or "Edit a file".
    public let title: String
    /// The model's own description of the step, when the input carries one.
    public let headline: String?
    public let fields: [ToolPermissionField]
    public static let maximumFieldBytes = 4_096
    public static let maximumFields = 12

    public init(title: String, headline: String? = nil, fields: [ToolPermissionField] = []) {
        self.title = title; self.headline = headline; self.fields = fields
    }

    /// The first monospaced value, which the compact pet bubble shows.
    public var primaryCode: ToolPermissionField? { fields.first { $0.code } }

    public static func make(toolName: String, inputJSON: String) -> ToolPermissionPresentation {
        let name = toolName.trimmingCharacters(in: .whitespacesAndNewlines)
        let input = (inputJSON.data(using: .utf8).flatMap { try? JSONSerialization.jsonObject(with: $0) } as? [String: Any]) ?? [:]
        if name == "command_execution" || name == "file_change" {
            return codexPresentation(name: name, input: input)
        }
        func text(_ key: String) -> String? {
            guard let raw = input[key] else { return nil }
            let string: String
            if let value = raw as? String { string = value }
            else if let value = raw as? [String] { string = value.joined(separator: "\n") }
            else if let number = raw as? NSNumber { string = CFGetTypeID(number) == CFBooleanGetTypeID() ? (number.boolValue ? L("permission.value.yes") : L("permission.value.no")) : number.stringValue }
            else if JSONSerialization.isValidJSONObject(raw), let data = try? JSONSerialization.data(withJSONObject: raw, options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]) { string = String(decoding: data, as: UTF8.self) }
            else { return nil }
            let clean = ActivitySupport.clean(string, maximumBytes: maximumFieldBytes)
            guard !clean.isEmpty else { return nil }
            return string.utf8.count > maximumFieldBytes ? clean + "\n…" : clean
        }
        func field(_ key: String, _ label: String, code: Bool = false) -> ToolPermissionField? {
            text(key).map { ToolPermissionField(label: label, value: $0, code: code) }
        }
        let headline = text("description").map { ActivitySupport.clean($0, maximumBytes: 1_000, singleLine: true) }
        var title: String
        var fields: [ToolPermissionField?] = []
        var consumed: Set<String> = ["description"]
        switch name {
        case "Bash":
            title = L("permission.tool.bash")
            fields = [field("command", L("permission.field.command"), code: true), field("timeout", L("permission.field.timeout")), field("run_in_background", L("permission.field.runInBackground"))]
            consumed.formUnion(["command", "timeout", "run_in_background"])
        case "Read":
            title = L("permission.tool.read")
            fields = [field("file_path", L("permission.field.file"), code: true), field("offset", L("permission.field.offset")), field("limit", L("permission.field.limit"))]
            consumed.formUnion(["file_path", "offset", "limit"])
        case "Edit", "MultiEdit":
            title = L("permission.tool.edit")
            fields = [field("file_path", L("permission.field.file"), code: true), field("old_string", L("permission.field.oldString"), code: true), field("new_string", L("permission.field.newString"), code: true), field("replace_all", L("permission.field.replaceAll")), field("edits", L("permission.field.edits"), code: true)]
            consumed.formUnion(["file_path", "old_string", "new_string", "replace_all", "edits"])
        case "Write":
            title = L("permission.tool.write")
            fields = [field("file_path", L("permission.field.file"), code: true), field("content", L("permission.field.content"), code: true)]
            consumed.formUnion(["file_path", "content"])
        case "NotebookEdit":
            title = L("permission.tool.notebook")
            fields = [field("notebook_path", L("permission.field.notebook"), code: true), field("cell_id", L("permission.field.cell")), field("edit_mode", L("permission.field.editMode")), field("new_source", L("permission.field.newString"), code: true)]
            consumed.formUnion(["notebook_path", "cell_id", "edit_mode", "new_source", "cell_type"])
        case "Glob", "Grep":
            title = name == "Glob" ? L("permission.tool.glob") : L("permission.tool.grep")
            fields = [field("pattern", L("permission.field.pattern"), code: true), field("path", L("permission.field.path"), code: true), field("glob", L("permission.field.glob"), code: true)]
            consumed.formUnion(["pattern", "path", "glob"])
        case "WebFetch":
            title = L("permission.tool.webFetch")
            fields = [field("url", L("permission.field.url"), code: true), field("prompt", L("permission.field.prompt"))]
            consumed.formUnion(["url", "prompt"])
        case "WebSearch":
            title = L("permission.tool.webSearch")
            fields = [field("query", L("permission.field.query"), code: true)]
            consumed.insert("query")
        case "Agent", "Task":
            title = L("permission.tool.agent")
            fields = [field("subagent_type", L("permission.field.subagentType")), field("model", L("permission.field.model")), field("prompt", L("permission.field.instructions"), code: true)]
            consumed.formUnion(["subagent_type", "model", "prompt", "name"])
        default:
            if name.hasPrefix("mcp__") {
                let parts = name.split(separator: "_", omittingEmptySubsequences: true).map(String.init)
                let server = parts.count >= 2 ? parts[1] : name
                title = L("permission.tool.mcp", ["server": ActivitySupport.clean(server, maximumBytes: 80, singleLine: true)])
            } else { title = L("permission.tool.tool") }
        }
        // Remaining keys keep their own names so nothing in the input is hidden.
        let preferred = ["command", "file_path", "path", "pattern", "query", "url", "prompt", "content"]
        let rest = input.keys.filter { !consumed.contains($0) }.sorted { lhs, rhs in
            let l = preferred.firstIndex(of: lhs) ?? preferred.count, r = preferred.firstIndex(of: rhs) ?? preferred.count
            return l == r ? lhs < rhs : l < r
        }
        for key in rest { fields.append(field(key, key, code: preferred.contains(key))) }
        return ToolPermissionPresentation(title: title, headline: headline, fields: Array(fields.compactMap { $0 }.prefix(maximumFields)))
    }

    /// Codex's channel rejects oversized requests before they reach the card.
    /// Show every approved command/diff byte here instead of a truncated preview.
    private static func codexPresentation(name: String, input: [String: Any]) -> ToolPermissionPresentation {
        func display(_ raw: Any?) -> String? {
            guard let raw, !(raw is NSNull) else { return nil }
            let text: String
            if let value = raw as? String { text = value }
            else if JSONSerialization.isValidJSONObject(raw),
                    let data = try? JSONSerialization.data(withJSONObject: raw, options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]) {
                text = String(decoding: data, as: UTF8.self)
            } else { text = String(describing: raw) }
            // Keep line breaks/tab layout, but render other control characters
            // and invisible direction/format marks literally rather than hiding them.
            return text.unicodeScalars.map { scalar in
                let category = scalar.properties.generalCategory
                if category == .format || (category == .control && scalar.value != 10 && scalar.value != 9) {
                    return String(scalar).utf16.map { String(format: "\\u%04x", $0) }.joined()
                }
                return String(scalar)
            }.joined()
        }
        var fields: [ToolPermissionField] = []
        func append(_ raw: Any?, _ label: String, code: Bool = true) {
            if let value = display(raw), !value.isEmpty { fields.append(ToolPermissionField(label: label, value: value, code: code)) }
        }
        var consumed: Set<String> = ["threadId", "turnId", "itemId", "reason"]
        if name == "command_execution" {
            append(input["command"], L("permission.field.command"))
            append(input["cwd"], L("permission.field.cwd"))
            if let network = input["networkApprovalContext"] as? [String: Any] {
                append(network["host"], L("permission.field.networkHost"))
                append(network["protocol"], L("permission.field.networkProtocol"))
                let extra = network.filter { $0.key != "host" && $0.key != "protocol" }
                if !extra.isEmpty { append(extra, L("permission.field.networkExtra")) }
            } else { append(input["networkApprovalContext"], L("permission.field.network")) }
            consumed.formUnion(["command", "cwd", "networkApprovalContext"])
        } else {
            append(input["changes"], L("permission.field.changes"))
            append(input["grantRoot"], L("permission.field.grantRoot"))
            consumed.formUnion(["changes", "grantRoot"])
        }
        append(input["reason"], L("permission.field.reason"), code: false)
        let remaining = input.filter { !consumed.contains($0.key) }
        if !remaining.isEmpty { append(remaining, L("permission.field.extra")) }
        return ToolPermissionPresentation(title: name == "command_execution" ? L("permission.tool.bash") : L("permission.tool.edit"), fields: fields)
    }

}
