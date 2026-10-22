import Foundation

// Antigravity permission cards (docs/superpowers/plans/2026-10-22-antigravity-permission-cards.md).
// A headless `agy -p` turn cannot ask: what its review mode would have asked
// about is declined and the turn goes on. The stream says exactly what was
// declined, so once the turn is over the pane asks the way a Claude card
// does, and an allow becomes a rule in agy's own settings
// (`~/.gemini/antigravity-cli/settings.json`, `permissions.allow`) for a
// follow-up turn in the same conversation.

/// One action a turn was refused.
public struct AntigravityDeniedAction: Codable, Sendable, Equatable {
    public enum Kind: String, Codable, Sendable, CaseIterable {
        case command, url, file, other
        /// A kind a newer host sends reads as `other`.
        public init(from decoder: Decoder) throws { self = Kind(rawValue: try decoder.singleValueContainer().decode(String.self)) ?? .other }
    }
    public var kind: Kind
    /// agy's permission name: `command`, `write_file`, `read_file`,
    /// `read_url`, `execute_url`, `mcp`, or what the tool itself is called.
    public var action: String
    /// agy's own name for it (`RunCommand`, `WriteFile`), else the tool's.
    public var displayName: String
    /// The command line, the domain or the path; nil when agy did not say.
    public var target: String?
    /// The `permissions.allow` rule that allows exactly this; nil when none is
    /// known (the card then says to change the permission mode instead).
    public var rule: String?
    public init(kind: Kind, action: String, displayName: String, target: String? = nil, rule: String? = nil) {
        self.kind = kind; self.action = action; self.displayName = displayName; self.target = target; self.rule = rule
    }
}

/// The card one Antigravity turn leaves: what it was refused, together.
public struct AntigravityPermissionRequest: Codable, Sendable, Equatable, Identifiable {
    public static let maximumActions = 16
    /// `agy-<uuid>`; the phone answers by this id and `runId`.
    public var id: String
    /// The run that was refused (its activity id).
    public var runId: String
    public var actions: [AntigravityDeniedAction]
    /// ISO 8601, when the turn ended.
    public var receivedAt: String?
    public init(id: String = "agy-" + UUID().uuidString.lowercased(), runId: String, actions: [AntigravityDeniedAction], receivedAt: String? = mightyTimestamp()) {
        self.id = id; self.runId = runId; self.actions = actions; self.receivedAt = receivedAt
    }
    /// The rules an allow adds, once each, in order.
    public var rules: [String] {
        var seen = Set<String>()
        return actions.compactMap(\.rule).filter { seen.insert($0).inserted }
    }
    /// At least one action can be allowed with a rule.
    public var canAllow: Bool { !rules.isEmpty }
    /// What the transcript line and the continuation name: the targets, else agy's names.
    public var names: [String] { actions.map { $0.target ?? $0.displayName } }
}

/// The three buttons.
public enum AntigravityPermissionDecision: String, Codable, Sendable, CaseIterable {
    case once, always, deny
}

/// What the follow-up run of an allowed card carries: the rules to put in
/// place while it runs (`temporary`) or for good. `requestId` is the card's:
/// a host that runs remote panes adds only rules it reported itself.
public struct AntigravityAllowGrant: Codable, Sendable, Equatable {
    public static let maximumRuleBytes = 4_096
    public var requestId: String
    public var rules: [String]
    public var temporary: Bool
    public init(requestId: String, rules: [String], temporary: Bool) { self.requestId = requestId; self.rules = rules; self.temporary = temporary }
    /// A rule is `name(…)`: a lowercase permission name and a non-empty
    /// argument, at most 4 KiB, no NUL, no line break.
    public static func validRule(_ rule: String) -> Bool {
        guard rule.utf8.count <= maximumRuleBytes, !rule.contains("\0"), !rule.contains("\n"), !rule.contains("\r") else { return false }
        return rule.range(of: "\\A[a-z][a-z_]{0,63}\\(.+\\)\\z", options: .regularExpression) != nil
    }
    public var isValid: Bool {
        CoreValidation.identifier(requestId) && !rules.isEmpty && rules.count <= AntigravityPermissionRequest.maximumActions && rules.allSatisfy(Self.validRule)
    }
}

// MARK: - Reading the stream

/// Collects one turn's refusals from agy's stream-json: each tool step that
/// ended `ERROR` with a permission denial ("permission check failed … user
/// denied permission …"), then the result's `denied_actions`.
public struct AntigravityDenialCollector: Sendable {
    private var steps: [Int: AntigravityDeniedAction] = [:]
    private var order: [Int] = []
    public init() {}

    /// A denial's message, or nil when the step's error is something else.
    public static func denialMessage(_ info: [String: Any]?) -> String? {
        guard let error = info?["error"] else { return nil }
        let message = (error as? [String: Any])?["message"] as? String ?? error as? String
        guard let message, message.utf8.count <= 65_536 else { return nil }
        let lowered = message.lowercased()
        return lowered.contains("permission check failed") && lowered.contains("denied permission") ? message : nil
    }

    /// One `step_update` payload of a tool step.
    public mutating func observe(step payload: [String: Any]) {
        guard (payload["state"] as? String)?.uppercased() == "ERROR", let index = payload["step_index"] as? Int else { return }
        let info = payload["tool_info"] as? [String: Any]
        guard let message = Self.denialMessage(info) else { return }
        let tool = info?["name"] as? String ?? payload["tool_name"] as? String ?? "tool"
        if steps[index] == nil { order.append(index) }
        steps[index] = Self.action(tool: String(tool.prefix(128)), parameters: info?["parameters"] as? [String: Any], message: message)
    }

    /// The turn's `result` payload: every refusal, once each. Starts over for the next turn.
    public mutating func finish(result payload: [String: Any]) -> [AntigravityDeniedAction] {
        defer { steps = [:]; order = [] }
        let reported = (payload["denied_actions"] as? [[String: Any]] ?? []).prefix(AntigravityPermissionRequest.maximumActions).compactMap { item -> (String, String)? in
            guard let action = (item["action"] as? String).map({ String($0.prefix(128)) }), !action.isEmpty else { return nil }
            return (action, (item["display_name"] as? String).map { String($0.prefix(128)) } ?? action)
        }
        var result: [AntigravityDeniedAction] = []
        var seen = Set<String>()
        func add(_ value: AntigravityDeniedAction) {
            let key = value.rule ?? "\(value.kind.rawValue)|\(value.action)|\(value.target ?? "")"
            guard result.count < AntigravityPermissionRequest.maximumActions, seen.insert(key).inserted else { return }
            result.append(value)
        }
        for index in order {
            guard var value = steps[index] else { continue }
            if let name = reported.first(where: { $0.0 == value.action })?.1 { value.displayName = name }
            add(value)
        }
        // A refusal the steps did not show (agy reports each permission once).
        let covered = Set(result.map(\.action))
        for (action, name) in reported where !covered.contains(action) {
            add(AntigravityDeniedAction(kind: Self.kind(action: action, tool: action), action: action, displayName: name))
        }
        return result
    }

    static func kind(action: String, tool: String) -> AntigravityDeniedAction.Kind {
        let name = action.lowercased(), toolName = tool.lowercased()
        if name == "command" || toolName == "run_command" { return .command }
        if name.hasSuffix("_url") || name == "url" || toolName.contains("url") || toolName.contains("browser") { return .url }
        if name.hasSuffix("_file") || ["write_to_file", "replace_file_content", "multi_replace_file_content", "sed_file", "notebook_edit", "view_file"].contains(toolName) { return .file }
        return .other
    }

    /// What one denied step was. A command is allowed by its whole command
    /// line (`command(<line>)`, a prefix rule in agy); any other denial names
    /// its own rule ("user denied permission for write_file(/path)"), which is
    /// used as agy wrote it.
    static func action(tool: String, parameters: [String: Any]?, message: String) -> AntigravityDeniedAction {
        func parameter(_ names: [String]) -> String? {
            for name in names { if let value = parameters?[name] as? String, !value.isEmpty { return String(value.prefix(AntigravityAllowGrant.maximumRuleBytes)) } }
            return nil
        }
        if tool == "run_command" || message.lowercased().contains("denied permission to run command") {
            let line = parameter(["CommandLine", "Command", "command"])
            let rule = line.map { "command(\($0))" }.flatMap { AntigravityAllowGrant.validRule($0) ? $0 : nil }
            return AntigravityDeniedAction(kind: .command, action: "command", displayName: tool, target: line, rule: rule)
        }
        if let rule = namedRule(message) {
            let name = String(rule[..<rule.firstIndex(of: "(")!])
            var target = String(rule[rule.index(after: rule.firstIndex(of: "(")!)...].dropLast())
            let kind = Self.kind(action: name, tool: tool)
            // A URL rule names a domain; show the domain, as the rule does.
            if kind == .url, let host = URL(string: target)?.host, !host.isEmpty { target = host }
            return AntigravityDeniedAction(kind: kind, action: name, displayName: tool, target: target, rule: rule)
        }
        let kind = Self.kind(action: tool, tool: tool)
        var target: String?
        switch kind {
        case .url: target = parameter(["Url", "URL", "url"]).map { URL(string: $0)?.host ?? $0 }
        case .file: target = parameter(["Path", "TargetFile", "AbsolutePath", "path"])
        case .command: target = parameter(["CommandLine"])
        case .other: target = nil
        }
        return AntigravityDeniedAction(kind: kind, action: tool, displayName: tool, target: target)
    }

    /// The rule a denial names: the text after "denied permission for " up to
    /// the end of its line, when it is a whole `name(…)` rule.
    static func namedRule(_ message: String) -> String? {
        guard let range = message.range(of: "denied permission for ", options: .caseInsensitive) else { return nil }
        let rest = message[range.upperBound...]
        let line = rest.prefix { $0 != "\n" && $0 != "\r" }.trimmingCharacters(in: .whitespaces)
        // "User denied permission for write_file(/x)." ends its sentence.
        let rule = line.hasSuffix(").") ? String(line.dropLast()) : line
        return AntigravityAllowGrant.validRule(rule) ? rule : nil
    }
}

// MARK: - The card in a pane

/// The cards of every pane: one per pane, the last turn's. A card stays
/// until it is answered, or until a later run of that pane ends — that
/// run's own card (if any) replaces it, and one without any leaves none.
public struct AntigravityPermissionBoard: Sendable, Equatable {
    public private(set) var cards: [String: AntigravityPermissionRequest] = [:]
    /// Panes whose card came from the run that is still ending.
    private var fromRunningTurn: Set<String> = []
    public init() {}

    public subscript(sessionId: String) -> AntigravityPermissionRequest? { cards[sessionId] }

    /// A turn ended with refusals (before its run's final status).
    public mutating func received(_ card: AntigravityPermissionRequest, sessionId: String) {
        cards[sessionId] = card; fromRunningTurn.insert(sessionId)
    }
    /// A run of the pane ended (completed, error or stopped).
    public mutating func runEnded(sessionId: String) {
        if fromRunningTurn.remove(sessionId) == nil { cards.removeValue(forKey: sessionId) }
    }
    /// Puts back a card whose answer could not run; the next run's end clears it as usual.
    public mutating func restore(_ card: AntigravityPermissionRequest, sessionId: String) {
        guard cards[sessionId] == nil else { return }
        cards[sessionId] = card
    }
    @discardableResult
    public mutating func remove(sessionId: String) -> AntigravityPermissionRequest? {
        fromRunningTurn.remove(sessionId)
        return cards.removeValue(forKey: sessionId)
    }
    public func pending(sessionId: String, requestId: String, runId: String) -> AntigravityPermissionRequest? {
        cards[sessionId].flatMap { $0.id == requestId && $0.runId == runId ? $0 : nil }
    }
}

/// What an answer does, decided without the app.
public struct AntigravityContinuation: Sendable, Equatable {
    public var grant: AntigravityAllowGrant
    /// The user turn sent on the same conversation.
    public var prompt: String
}

public enum AntigravityPermissionFlow {
    /// nil for `deny`, and for an allow of a card no rule can allow.
    public static func continuation(_ card: AntigravityPermissionRequest, decision: AntigravityPermissionDecision) -> AntigravityContinuation? {
        guard decision != .deny, card.canAllow else { return nil }
        let allowed = card.actions.filter { $0.rule != nil }.map { $0.target ?? $0.displayName }
        let list = ActivitySupport.clean(allowed.joined(separator: ", "), maximumBytes: 2_048, singleLine: true)
        return AntigravityContinuation(grant: AntigravityAllowGrant(requestId: card.id, rules: card.rules, temporary: decision == .once),
                                       prompt: L("antigravity.permission.continue", ["actions": list]))
    }

    /// The short transcript line a card leaves (what an older phone shows).
    public static func transcriptLine(_ card: AntigravityPermissionRequest) -> String {
        L("run.notice.antigravityAsked", ["actions": ActivitySupport.clean(card.names.joined(separator: ", "), maximumBytes: 512, singleLine: true)])
    }

    /// The card's label for an action's kind.
    public static func kindLabel(_ kind: AntigravityDeniedAction.Kind) -> String {
        switch kind {
        case .command: return L("antigravity.permission.kind.command")
        case .url: return L("antigravity.permission.kind.url")
        case .file: return L("antigravity.permission.kind.file")
        case .other: return L("antigravity.permission.kind.other")
        }
    }
}

// MARK: - agy's settings file

public enum AntigravitySettingsError: Error, LocalizedError, Equatable {
    case missingFolder(String)
    case unreadable(String)
    case notJSON(String)
    case notObject(String)
    case permissionsNotObject(String)
    case allowNotArray(String)
    case writeFailed(String, String)
    case badRule

    public var errorDescription: String? {
        switch self {
        case .missingFolder(let path): return L("antigravity.settings.error.missingFolder", ["path": path])
        case .unreadable(let path): return L("antigravity.settings.error.unreadable", ["path": path])
        case .notJSON(let path): return L("antigravity.settings.error.notJSON", ["path": path])
        case .notObject(let path): return L("antigravity.settings.error.notObject", ["path": path])
        case .permissionsNotObject(let path): return L("antigravity.settings.error.permissionsNotObject", ["path": path])
        case .allowNotArray(let path): return L("antigravity.settings.error.allowNotArray", ["path": path])
        case .writeFailed(let path, let error): return L("antigravity.settings.error.writeFailed", ["path": path, "error": error])
        case .badRule: return L("antigravity.settings.error.badRule")
        }
    }
}

/// Edits `permissions.allow` in the bytes of agy's settings, leaving every
/// other byte as it was: other keys, their order and values, unknown fields,
/// whitespace. Rules are added after the last one in the style of the list
/// (or of the file, for a list it has to create), and removing one takes out
/// exactly what adding it put in.
public enum AntigravitySettingsText {
    /// The file's rules; throws when it is not an object, `permissions` is not
    /// an object or `allow` is not an array (non-string entries are skipped).
    public static func rules(_ data: Data, path: String) throws -> [String] {
        guard let object = try? JSONSerialization.jsonObject(with: data, options: [.fragmentsAllowed]) else { throw AntigravitySettingsError.notJSON(path) }
        guard let top = object as? [String: Any] else { throw AntigravitySettingsError.notObject(path) }
        guard let permissions = top["permissions"] else { return [] }
        guard let section = permissions as? [String: Any] else { throw AntigravitySettingsError.permissionsNotObject(path) }
        guard let allow = section["allow"] else { return [] }
        guard let list = allow as? [Any] else { throw AntigravitySettingsError.allowNotArray(path) }
        return list.compactMap { $0 as? String }
    }

    /// A new settings file that holds only these rules.
    public static func newFile(_ rules: [String]) -> Data {
        Data(("{\n  \"permissions\": {\n    \"allow\": " + array(rules, indent: "    ", unit: "  ") + "\n  }\n}\n").utf8)
    }

    /// `rules` not in the file yet, added. Returns the bytes and what was added.
    public static func adding(_ rules: [String], to data: Data, path: String) throws -> (data: Data, added: [String]) {
        let existing = Set(try Self.rules(data, path: path))
        var seen = Set<String>()
        let missing = rules.filter { !existing.contains($0) && seen.insert($0).inserted }
        guard !missing.isEmpty else { return (data, []) }
        var b = [UInt8](data)
        let scan = Scanner(bytes: b, path: path)
        let top = scan.skipWhitespace(0)
        guard top < b.count, b[top] == UInt8(ascii: "{") else { throw AntigravitySettingsError.notObject(path) }
        let topObject = try scan.object(top)
        let unit = scan.lineIndent(topObject.members.first?.keyStart ?? -1).flatMap { $0.isEmpty ? nil : $0 } ?? "  "
        guard let permissions = topObject.members.last(where: { $0.key == "permissions" }) else {
            let text = "\"permissions\": "
            try insertMember(&b, scan: scan, object: topObject, indentOfObject: "", unit: unit) { indent in
                text + allowObject(missing, indent: indent, unit: unit)
            }
            return (Data(b), missing)
        }
        let permissionsIndent = scan.lineIndent(permissions.keyStart)
        let section = try scan.object(permissions.valueStart)
        guard let allow = section.members.last(where: { $0.key == "allow" }) else {
            try insertMember(&b, scan: scan, object: section, indentOfObject: permissionsIndent, unit: unit) { indent in
                "\"allow\": " + array(missing, indent: indent, unit: unit)
            }
            return (Data(b), missing)
        }
        let list = try scan.array(allow.valueStart)
        if let last = list.elements.last {
            let separator = Array(b[scan.separatorEnd(before: last.lowerBound, after: list.elements.count > 1 ? list.elements[list.elements.count - 2].upperBound : list.open)..<last.lowerBound])
            var inserted: [UInt8] = []
            for rule in missing { inserted += Array(",".utf8) + separator + encoded(rule) }
            b.insert(contentsOf: inserted, at: last.upperBound)
        } else {
            let indent = scan.lineIndent(allow.keyStart)
            let rendered = Array(array(missing, indent: indent, unit: unit).utf8)
            b.replaceSubrange(list.open...list.close, with: rendered)
        }
        return (Data(b), missing)
    }

    /// One occurrence of each of `rules` taken out of `permissions.allow`
    /// (the last one). Returns the bytes and what was removed.
    public static func removing(_ rules: [String], from data: Data, path: String) throws -> (data: Data, removed: [String]) {
        _ = try Self.rules(data, path: path)
        var b = [UInt8](data)
        var removed: [String] = []
        for rule in Set(rules).sorted() {
            let scan = Scanner(bytes: b, path: path)
            let top = scan.skipWhitespace(0)
            let topObject = try scan.object(top)
            guard let permissions = topObject.members.last(where: { $0.key == "permissions" }) else { break }
            let section = try scan.object(permissions.valueStart)
            guard let allow = section.members.last(where: { $0.key == "allow" }) else { break }
            let list = try scan.array(allow.valueStart)
            guard let index = list.elements.lastIndex(where: { scan.string($0) == rule }) else { continue }
            let elements = list.elements
            if elements.count == 1 { b.replaceSubrange((list.open + 1)..<list.close, with: []) }
            else if index == elements.count - 1 { b.removeSubrange(elements[index - 1].upperBound..<elements[index].upperBound) }
            else { b.removeSubrange(elements[index].lowerBound..<elements[index + 1].lowerBound) }
            removed.append(rule)
        }
        // Whatever is left must still read as the same kind of file.
        _ = try Self.rules(Data(b), path: path)
        return (Data(b), removed)
    }

    // MARK: Rendering

    static func encoded(_ rule: String) -> [UInt8] {
        let data = (try? JSONSerialization.data(withJSONObject: rule, options: [.fragmentsAllowed, .withoutEscapingSlashes])) ?? Data("\"\"".utf8)
        return [UInt8](data)
    }
    /// A list on its own lines under `indent`, or on one line when the place it goes is one line.
    static func array(_ rules: [String], indent: String?, unit: String) -> String {
        let items = rules.map { String(decoding: encoded($0), as: UTF8.self) }
        guard let indent else { return "[" + items.joined(separator: ", ") + "]" }
        return "[\n" + items.map { indent + unit + $0 }.joined(separator: ",\n") + "\n" + indent + "]"
    }
    static func allowObject(_ rules: [String], indent: String?, unit: String) -> String {
        guard let indent else { return "{\"allow\": " + array(rules, indent: nil, unit: unit) + "}" }
        return "{\n" + indent + unit + "\"allow\": " + array(rules, indent: indent + unit, unit: unit) + "\n" + indent + "}"
    }

    /// Adds a member to `object`: after its last member, in that member's
    /// style, or as the only member of an empty object. `render` gets the
    /// member's indent (nil when it goes on one line).
    private static func insertMember(_ b: inout [UInt8], scan: Scanner, object: Scanner.Object, indentOfObject: String?, unit: String, render: (String?) -> String) throws {
        if let last = object.members.last {
            let previousEnd = object.members.count > 1 ? object.members[object.members.count - 2].valueEnd : object.open
            let separator = Array(b[scan.separatorEnd(before: last.keyStart, after: previousEnd)..<last.keyStart])
            let indent = scan.lineIndent(last.keyStart)
            b.insert(contentsOf: Array(",".utf8) + separator + Array(render(indent).utf8), at: last.valueEnd)
        } else if let outer = indentOfObject {
            let text = "{\n" + outer + unit + render(outer + unit) + "\n" + outer + "}"
            b.replaceSubrange(object.open...object.close, with: Array(text.utf8))
        } else {
            b.replaceSubrange(object.open...object.close, with: Array(("{" + render(nil) + "}").utf8))
        }
    }

    /// Just enough of a JSON reader to find where things are. The bytes were
    /// already read by JSONSerialization; anything unexpected is `notJSON`.
    struct Scanner {
        let bytes: [UInt8]
        let path: String
        struct Member { var key: String; var keyStart: Int; var valueStart: Int; var valueEnd: Int }
        struct Object { var open: Int; var close: Int; var members: [Member] }
        struct List { var open: Int; var close: Int; var elements: [Range<Int>] }

        func fail() -> AntigravitySettingsError { .notJSON(path) }
        func skipWhitespace(_ start: Int) -> Int {
            var i = start
            while i < bytes.count, [0x20, 0x09, 0x0A, 0x0D].contains(bytes[i]) { i += 1 }
            return i
        }
        /// Index after the closing quote of the string at `start`.
        func stringEnd(_ start: Int) throws -> Int {
            guard start < bytes.count, bytes[start] == UInt8(ascii: "\"") else { throw fail() }
            var i = start + 1
            while i < bytes.count {
                if bytes[i] == UInt8(ascii: "\\") { i += 2; continue }
                if bytes[i] == UInt8(ascii: "\"") { return i + 1 }
                i += 1
            }
            throw fail()
        }
        func string(_ range: Range<Int>) -> String? {
            guard range.lowerBound < bytes.count, bytes[range.lowerBound] == UInt8(ascii: "\"") else { return nil }
            return (try? JSONSerialization.jsonObject(with: Data(bytes[range]), options: [.fragmentsAllowed])) as? String
        }
        func valueEnd(_ start: Int) throws -> Int {
            guard start < bytes.count else { throw fail() }
            switch bytes[start] {
            case UInt8(ascii: "\""): return try stringEnd(start)
            case UInt8(ascii: "{"): return try object(start).close + 1
            case UInt8(ascii: "["): return try array(start).close + 1
            default:
                var i = start
                while i < bytes.count, ![UInt8(ascii: ","), UInt8(ascii: "]"), UInt8(ascii: "}"), 0x20, 0x09, 0x0A, 0x0D].contains(bytes[i]) { i += 1 }
                guard i > start else { throw fail() }
                return i
            }
        }
        func object(_ open: Int) throws -> Object {
            guard open < bytes.count, bytes[open] == UInt8(ascii: "{") else { throw fail() }
            var members: [Member] = []
            var i = skipWhitespace(open + 1)
            if i < bytes.count, bytes[i] == UInt8(ascii: "}") { return Object(open: open, close: i, members: []) }
            while true {
                let keyStart = i
                let keyEnd = try stringEnd(keyStart)
                guard let key = string(keyStart..<keyEnd) else { throw fail() }
                i = skipWhitespace(keyEnd)
                guard i < bytes.count, bytes[i] == UInt8(ascii: ":") else { throw fail() }
                let valueStart = skipWhitespace(i + 1)
                let end = try valueEnd(valueStart)
                members.append(Member(key: key, keyStart: keyStart, valueStart: valueStart, valueEnd: end))
                i = skipWhitespace(end)
                guard i < bytes.count else { throw fail() }
                if bytes[i] == UInt8(ascii: "}") { return Object(open: open, close: i, members: members) }
                guard bytes[i] == UInt8(ascii: ",") else { throw fail() }
                i = skipWhitespace(i + 1)
            }
        }
        func array(_ open: Int) throws -> List {
            guard open < bytes.count, bytes[open] == UInt8(ascii: "[") else { throw AntigravitySettingsError.allowNotArray(path) }
            var elements: [Range<Int>] = []
            var i = skipWhitespace(open + 1)
            if i < bytes.count, bytes[i] == UInt8(ascii: "]") { return List(open: open, close: i, elements: []) }
            while true {
                let end = try valueEnd(i)
                elements.append(i..<end)
                i = skipWhitespace(end)
                guard i < bytes.count else { throw fail() }
                if bytes[i] == UInt8(ascii: "]") { return List(open: open, close: i, elements: elements) }
                guard bytes[i] == UInt8(ascii: ",") else { throw fail() }
                i = skipWhitespace(i + 1)
            }
        }
        /// Where the whitespace before `position` starts, after the comma (or
        /// bracket) that follows `after`.
        func separatorEnd(before position: Int, after: Int) -> Int {
            var i = position
            while i > after + 1, [0x20, 0x09, 0x0A, 0x0D].contains(bytes[i - 1]) { i -= 1 }
            return i
        }
        /// The spaces and tabs before `position` when nothing else is on its line before it.
        func lineIndent(_ position: Int) -> String? {
            guard position >= 0, position <= bytes.count else { return nil }
            var i = position
            while i > 0, bytes[i - 1] == 0x20 || bytes[i - 1] == 0x09 { i -= 1 }
            guard i == 0 || bytes[i - 1] == 0x0A else { return nil }
            return String(decoding: bytes[i..<position], as: UTF8.self)
        }
    }
}

// MARK: - The file and the rules the app holds

/// Puts rules in agy's settings and takes them out again. One per app: the
/// panes on this Mac and the runs it hosts for other computers share it, so a
/// rule two of them hold at once stays until the last is done. Every rule it
/// adds for a while is written to its ledger first, and taken out of it only
/// after the settings no longer have it, so a crash leaves nothing a next
/// launch (`removeLeftovers`) does not clean up. A rule the settings already
/// had is never removed.
public actor AntigravityRuleLeases {
    public struct Lease: Sendable, Hashable {
        public let id = UUID()
        /// The rules this lease holds a count on (the ones the app added).
        public let rules: [String]
    }
    private struct LedgerEntry: Codable, Equatable { var settings: String; var rule: String }
    private struct Ledger: Codable { var version = 1; var entries: [LedgerEntry] = [] }

    public let settingsURL: URL
    private let ledgerURL: URL?
    private var counts: [String: Int] = [:]
    private var leases: Set<Lease> = []

    /// `~/.gemini/antigravity-cli/settings.json` (on Windows the user profile's same path).
    public static func defaultSettingsURL(home: URL = FileManager.default.homeDirectoryForCurrentUser) -> URL {
        home.appendingPathComponent(".gemini/antigravity-cli/settings.json")
    }

    /// `ledgerURL`: where the rules added for a while are recorded (the app's
    /// data folder); nil keeps no record (tests of the counting alone).
    public init(settingsURL: URL = AntigravityRuleLeases.defaultSettingsURL(), ledgerURL: URL?) {
        self.settingsURL = settingsURL; self.ledgerURL = ledgerURL
    }

    /// The rules the app holds for a while now, with their counts.
    public func held() -> [String: Int] { counts }

    /// Reads the settings the way adding would, and changes nothing.
    public func check() throws {
        _ = try read()
    }

    /// Puts `grant`'s rules in place. A temporary grant returns the lease to
    /// `release` when its run ends; one for good returns nil.
    public func apply(_ grant: AntigravityAllowGrant) throws -> Lease? {
        guard grant.isValid else { throw AntigravitySettingsError.badRule }
        let current = try read()
        let existing = Set(current.rules)
        var seen = Set<String>()
        let rules = grant.rules.filter { seen.insert($0).inserted }
        guard grant.temporary else {
            // Kept for good: a rule the app holds for a while is no longer its to remove.
            let owned = rules.filter { counts[$0] != nil }
            try write(adding: rules.filter { !existing.contains($0) }, to: current)
            for rule in owned { counts.removeValue(forKey: rule) }
            try? forget(owned)
            return nil
        }
        var held: [String] = []
        var added: [String] = []
        for rule in rules {
            if let count = counts[rule] { counts[rule] = count + 1; held.append(rule) }
            else if !existing.contains(rule) { added.append(rule) }
        }
        if !added.isEmpty {
            try record(added)
            do { try write(adding: added, to: current) }
            catch { try? forget(added); for rule in held { counts[rule] = (counts[rule] ?? 1) - 1 }; throw error }
            for rule in added { counts[rule] = 1 }
            held += added
        }
        let lease = Lease(rules: held)
        leases.insert(lease)
        return lease
    }

    /// The lease's run ended: a rule nobody holds any more leaves the settings.
    public func release(_ lease: Lease) {
        guard leases.remove(lease) != nil else { return }
        var gone: [String] = []
        for rule in lease.rules {
            guard let count = counts[rule] else { continue }
            if count > 1 { counts[rule] = count - 1 } else { counts.removeValue(forKey: rule); gone.append(rule) }
        }
        remove(gone)
    }

    /// Quit: every rule held for a while leaves now.
    public func releaseAll() {
        let gone = Array(counts.keys)
        counts.removeAll(); leases.removeAll()
        remove(gone)
    }

    /// Launch: what a previous run of the app added for a while and could not take out.
    public func removeLeftovers() {
        let path = settingsURL.path
        let left = (readLedger()?.entries ?? []).filter { $0.settings == path }.map(\.rule).filter { counts[$0] == nil }
        remove(left)
    }

    // MARK: Files

    private struct Current { var data: Data?; var rules: [String] }

    private func read() throws -> Current {
        let path = settingsURL.path
        let target = settingsURL.resolvingSymlinksInPath()
        let manager = FileManager.default
        var isDirectory: ObjCBool = false
        guard manager.fileExists(atPath: target.deletingLastPathComponent().path, isDirectory: &isDirectory), isDirectory.boolValue else { throw AntigravitySettingsError.missingFolder(target.deletingLastPathComponent().path) }
        guard manager.fileExists(atPath: target.path) else { return Current(data: nil, rules: []) }
        guard let data = try? Data(contentsOf: target) else { throw AntigravitySettingsError.unreadable(path) }
        return Current(data: data, rules: try AntigravitySettingsText.rules(data, path: path))
    }

    private func write(adding rules: [String], to current: Current) throws {
        guard !rules.isEmpty else { return }
        let data = try current.data.map { try AntigravitySettingsText.adding(rules, to: $0, path: settingsURL.path).data } ?? AntigravitySettingsText.newFile(rules)
        try save(data)
    }

    /// Takes `rules` out of the settings, then out of the ledger. A file that
    /// cannot be read or written keeps them in the ledger for the next launch.
    private func remove(_ rules: [String]) {
        guard !rules.isEmpty else { return }
        do {
            let current = try read()
            if let data = current.data {
                let result = try AntigravitySettingsText.removing(rules, from: data, path: settingsURL.path)
                if !result.removed.isEmpty { try save(result.data) }
            }
            try forget(rules)
        } catch {}
    }

    /// Writes beside the file and renames over it, keeping its permissions; a
    /// symbolic link keeps pointing where it did.
    private func save(_ data: Data) throws {
        let target = settingsURL.resolvingSymlinksInPath()
        let manager = FileManager.default
        let temporary = target.deletingLastPathComponent().appendingPathComponent(".\(target.lastPathComponent).mighty-\(UUID().uuidString).tmp")
        let permissions = (try? manager.attributesOfItem(atPath: target.path))?[.posixPermissions] as? NSNumber
        do {
            guard manager.createFile(atPath: temporary.path, contents: data, attributes: [.posixPermissions: permissions ?? NSNumber(value: 0o644)]) else {
                throw CocoaError(.fileWriteUnknown)
            }
            guard rename(temporary.path, target.path) == 0 else { throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO) }
        } catch {
            try? manager.removeItem(at: temporary)
            throw AntigravitySettingsError.writeFailed(settingsURL.path, error.localizedDescription)
        }
    }

    private func readLedger() -> Ledger? {
        guard let ledgerURL, let data = try? Data(contentsOf: ledgerURL) else { return nil }
        return try? JSONDecoder().decode(Ledger.self, from: data)
    }
    private func writeLedger(_ ledger: Ledger) throws {
        guard let ledgerURL else { return }
        if ledger.entries.isEmpty { try? FileManager.default.removeItem(at: ledgerURL); return }
        try FileManager.default.createDirectory(at: ledgerURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        let encoder = JSONEncoder(); encoder.outputFormatting = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
        try encoder.encode(ledger).write(to: ledgerURL, options: .atomic)
    }
    private func record(_ rules: [String]) throws {
        guard ledgerURL != nil else { return }
        var ledger = readLedger() ?? Ledger()
        for rule in rules where !ledger.entries.contains(LedgerEntry(settings: settingsURL.path, rule: rule)) {
            ledger.entries.append(LedgerEntry(settings: settingsURL.path, rule: rule))
        }
        do { try writeLedger(ledger) } catch { throw AntigravitySettingsError.writeFailed(ledgerURL!.path, error.localizedDescription) }
    }
    private func forget(_ rules: [String]) throws {
        guard ledgerURL != nil, !rules.isEmpty, var ledger = readLedger() else { return }
        let gone = Set(rules)
        ledger.entries.removeAll { $0.settings == settingsURL.path && gone.contains($0.rule) }
        try writeLedger(ledger)
    }
}
