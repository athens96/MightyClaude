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
    /// Its rule may be allowed only for the follow-up run (`AntigravityRuleSafety`).
    public var onceOnly: Bool { rule.map { AntigravityRuleSafety.verdict($0) == .once } ?? false }
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
    /// The rules an allow adds, once each, in order (never one `AntigravityRuleSafety` refuses).
    public var rules: [String] {
        var seen = Set<String>()
        return actions.compactMap(\.rule).filter { AntigravityRuleSafety.verdict($0) != .refused && seen.insert($0).inserted }
    }
    /// At least one action can be allowed with a rule.
    public var canAllow: Bool { !rules.isEmpty }
    /// "항상 허용" is offered: every rule may be kept (none chains, pipes,
    /// redirects or runs a destructive command).
    public var canAllowAlways: Bool { canAllow && rules.allSatisfy { AntigravityRuleSafety.verdict($0) == .always } }
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
    /// Well formed, and no rule `AntigravityRuleSafety` refuses. Whether it
    /// may be kept for good is `keepsOnlyAllowedRules`.
    public var isValid: Bool {
        CoreValidation.identifier(requestId) && !rules.isEmpty && rules.count <= AntigravityPermissionRequest.maximumActions
            && rules.allSatisfy { Self.validRule($0) && AntigravityRuleSafety.verdict($0) != .refused }
    }
    /// A temporary grant, or one whose every rule may be kept.
    public var keepsOnlyAllowedRules: Bool { temporary || rules.allSatisfy { AntigravityRuleSafety.verdict($0) == .always } }
}

/// Which rules an allow may add. agy's rules are prefixes (probed on agy
/// 1.4.5: `command(echo hi)` allows `echo hi there`, `write_file(<folder>)`
/// everything in it, `command(*)` every command) and a temporary one applies
/// to every agy run on the computer while it is in place, so they are kept
/// narrow: a closed mapping from tool to permission, no wildcard, no whole
/// disk, no parenthesis, and a command that chains, pipes, redirects or
/// runs one of a closed list of destructive programs only once.
public enum AntigravityRuleSafety {
    public enum Verdict: String, Sendable, Codable, CaseIterable {
        /// Never offered or added.
        case refused
        /// Offered and added only for the follow-up run (no "항상 허용").
        case once
        /// May be kept for good.
        case always
    }

    /// The permission an agy tool's rule names; any other tool gets no rule.
    public static let toolPermissions: [String: String] = [
        "run_command": "command",
        "write_file": "write_file", "write_to_file": "write_file", "replace_file_content": "write_file", "multi_replace_file_content": "write_file",
        "sed_file": "write_file", "notebook_edit": "write_file",
        "view_file": "read_file", "read_file": "read_file", "list_dir": "read_file",
        "read_url_content": "read_url",
    ]
    /// The permissions a rule may name.
    public static let permissions: Set<String> = Set(toolPermissions.values)
    /// Programs that destroy data, escalate or end processes (compared by
    /// their name in lower case, without a folder or `.exe`); `mkfs…` too.
    public static let destructivePrograms: Set<String> = [
        "rm", "rmdir", "unlink", "shred", "dd", "sudo", "su", "doas", "kill", "killall", "pkill", "shutdown", "reboot", "halt", "poweroff", "eval",
        "del", "erase", "rd", "format", "remove-item", "taskkill", "stop-process", "stop-computer", "restart-computer",
    ]
    /// Characters that chain, pipe, redirect, background or substitute.
    public static let chainCharacters: Set<Character> = [";", "&", "|", ">", "<", "`"]

    /// `name` and the argument of a well-formed rule.
    public static func parts(_ rule: String) -> (name: String, argument: String)? {
        guard AntigravityAllowGrant.validRule(rule), let open = rule.firstIndex(of: "(") else { return nil }
        return (String(rule[..<open]), String(rule[rule.index(after: open)..<rule.index(before: rule.endIndex)]))
    }

    private final class Setting: @unchecked Sendable { let lock = NSLock(); var value: String? }
    private static let appData = Setting()
    /// The app's data folder (where the ledger lives); the app sets it at launch.
    /// No file rule may reach into it or above it.
    public static var appDataFolder: String? {
        get { appData.lock.lock(); defer { appData.lock.unlock() }; return appData.value }
        set { appData.lock.lock(); appData.value = newValue; appData.lock.unlock() }
    }

    /// The folders no file rule may reach into or above: agy's own `~/.gemini`
    /// (an always rule there would let agy rewrite its own permissions) and
    /// the app's data folder with its ledger, each as written and with its
    /// symbolic links resolved.
    public static func protectedFolders() -> [String] {
        var folders: [String] = []
        func add(_ url: URL) {
            folders.append(url.path)
            let resolved = url.resolvingSymlinksInPath().path
            if resolved != url.path { folders.append(resolved) }
        }
        add(FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".gemini", isDirectory: true))
        if let support = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first {
            add(support.appendingPathComponent("MightyClaude Native", isDirectory: true))
        }
        if let app = appDataFolder { add(URL(fileURLWithPath: app, isDirectory: true)) }
        return folders
    }

    /// `protectedFolders`: the folders file rules keep out of, compared as
    /// written (the shared cases); nil uses this computer's
    /// (`protectedFolders()`). `resolvingLinks` (by default when nil) also
    /// checks the rule's path with its symbolic links resolved.
    public static func verdict(_ rule: String, protectedFolders folders: [String]? = nil, resolvingLinks: Bool? = nil) -> Verdict {
        guard let (name, argument) = parts(rule), permissions.contains(name), !refusedArgument(argument) else { return .refused }
        if name == "write_file" || name == "read_file" {
            var paths = [argument]
            if resolvingLinks ?? (folders == nil), argument.hasPrefix("/") {
                let resolved = URL(fileURLWithPath: argument).resolvingSymlinksInPath().path
                if resolved != argument { paths.append(resolved) }
            }
            let guarded = (folders ?? protectedFolders()).compactMap(normalizedPath)
            for path in paths {
                guard let normalized = normalizedPath(path) else { return .refused }
                if guarded.contains(where: { within(normalized, $0) || within($0, normalized) }) { return .refused }
            }
        }
        // A command that names agy's own folder may run once, never for good.
        return name == "command" && (risky(argument) || argument.lowercased().contains(".gemini")) ? .once : .always
    }

    /// An absolute path, compared without case, `\\` as `/`, `.` and `..` taken
    /// out and no trailing slash; nil when it is not absolute (`/…`, `C:\…`).
    static func normalizedPath(_ path: String) -> String? {
        let slashed = path.replacingOccurrences(of: "\\", with: "/")
        let drive = slashed.range(of: "\\A[A-Za-z]:/", options: .regularExpression) != nil
        guard drive || slashed.hasPrefix("/") else { return nil }
        var parts: [String] = []
        for part in (drive ? String(slashed.dropFirst(2)) : slashed).split(separator: "/") {
            if part == "." { continue }
            if part == ".." { if !parts.isEmpty { parts.removeLast() }; continue }
            parts.append(part.lowercased())
        }
        return (drive ? String(slashed.prefix(2)).lowercased() : "") + "/" + parts.joined(separator: "/")
    }

    /// `path` is `folder` or inside it.
    static func within(_ path: String, _ folder: String) -> Bool {
        path == folder || path.hasPrefix(folder.hasSuffix("/") ? folder : folder + "/")
    }

    /// Empty, the whole disk or home (`/`, `~`, `C:\`), a wildcard, a parenthesis or a control character.
    static func refusedArgument(_ argument: String) -> Bool {
        if argument.contains("*") || argument.contains("(") || argument.contains(")") { return true }
        if argument.unicodeScalars.contains(where: { $0.value < 0x20 || $0.value == 0x7F }) { return true }
        var core = argument.trimmingCharacters(in: .whitespaces)
        while let last = core.last, last == "/" || last == "\\" { core.removeLast() }
        core = core.trimmingCharacters(in: .whitespaces)
        if core.isEmpty || core == "~" { return true }
        if core.count == 2, core.hasSuffix(":"), let drive = core.first, drive.isASCII, drive.isLetter { return true }
        return false
    }

    /// A command line that may be allowed only once.
    public static func risky(_ command: String) -> Bool {
        if command.contains(where: { chainCharacters.contains($0) }) || command.contains("$(") { return true }
        let words = command.split(whereSeparator: { $0 == " " || $0 == "\t" }).map { word -> String in
            var value = word.trimmingCharacters(in: CharacterSet(charactersIn: "\"'"))
            if let slash = value.lastIndex(where: { $0 == "/" || $0 == "\\" }) { value = String(value[value.index(after: slash)...]) }
            value = value.lowercased()
            if value.hasSuffix(".exe") { value.removeLast(4) }
            return value
        }
        if words.contains(where: { destructivePrograms.contains($0) || $0.hasPrefix("mkfs") }) { return true }
        let shortFlag = { (word: String, letter: Character) in word.hasPrefix("-") && !word.hasPrefix("--") && word.contains(letter) }
        if words.contains(where: { $0 == "chmod" || $0 == "chown" }), words.contains(where: { shortFlag($0, "r") || $0 == "--recursive" }) { return true }
        if words.contains("git") {
            if words.contains("push"), words.contains(where: { $0 == "-f" || $0.hasPrefix("--force") || $0.hasPrefix("+") || shortFlag($0, "f") }) { return true }
            if words.contains("reset"), words.contains("--hard") { return true }
            if words.contains("clean") { return true }
        }
        if words.contains("find"), words.contains("-delete") { return true }
        return false
    }
}

// MARK: - Reading the stream

/// Collects one turn's refusals from agy's stream-json: each tool step that
/// ended `ERROR` with a permission denial ("permission check failed … user
/// denied permission …") whose permission the result's `denied_actions`
/// lists, then what `denied_actions` lists that no step showed.
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
        // A step counts only when the result says its permission was refused.
        let listed = Set(reported.map(\.0))
        for index in order {
            guard var value = steps[index], listed.contains(value.action) else { continue }
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
    /// line (`command(<line>)`, a word-prefix rule in agy); any other denial
    /// names its own rule ("user denied permission for write_file(/path)"),
    /// used as agy wrote it when it names the tool's own permission
    /// (`AntigravityRuleSafety.toolPermissions`). A rule the safety rules
    /// refuse, or a tool outside the mapping, leaves the action without one.
    static func action(tool: String, parameters: [String: Any]?, message: String) -> AntigravityDeniedAction {
        func parameter(_ names: [String]) -> String? {
            for name in names { if let value = parameters?[name] as? String, !value.isEmpty { return String(value.prefix(AntigravityAllowGrant.maximumRuleBytes)) } }
            return nil
        }
        let permission = AntigravityRuleSafety.toolPermissions[tool]
        if permission == "command" {
            let line = parameter(["CommandLine", "Command", "command"])
            let rule = line.map { "command(\($0))" }.flatMap { AntigravityRuleSafety.verdict($0) == .refused ? nil : $0 }
            return AntigravityDeniedAction(kind: .command, action: "command", displayName: tool, target: line, rule: rule)
        }
        if let permission, let rule = namedRule(message), let (name, argument) = AntigravityRuleSafety.parts(rule), name == permission,
           AntigravityRuleSafety.verdict(rule) != .refused {
            var target = argument
            let kind = Self.kind(action: name, tool: tool)
            // A URL rule names a domain; show the domain, as the rule does.
            if kind == .url, let host = URL(string: target)?.host, !host.isEmpty { target = host }
            return AntigravityDeniedAction(kind: kind, action: name, displayName: tool, target: target, rule: rule)
        }
        let kind = Self.kind(action: permission ?? tool, tool: tool)
        var target: String?
        switch kind {
        case .url: target = parameter(["Url", "URL", "url"]).map { URL(string: $0)?.host ?? $0 }
        case .file: target = parameter(["Path", "TargetFile", "AbsolutePath", "path"])
        case .command: target = parameter(["CommandLine"])
        case .other: target = nil
        }
        return AntigravityDeniedAction(kind: kind, action: permission ?? tool, displayName: tool, target: target)
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
    /// Why an allow cannot go ahead: no rule can allow the card, or "항상
    /// 허용" on a card with a rule that may be allowed only once. nil for
    /// `deny` and for an allow that can.
    public static func refusal(_ card: AntigravityPermissionRequest, decision: AntigravityPermissionDecision) -> String? {
        guard decision != .deny else { return nil }
        guard card.canAllow else { return L("antigravity.permission.noRule") }
        if decision == .always, !card.canAllowAlways { return L("remote.error.antigravityOnceOnly") }
        return nil
    }

    /// nil for `deny`, and for an allow `refusal` refuses.
    public static func continuation(_ card: AntigravityPermissionRequest, decision: AntigravityPermissionDecision) -> AntigravityContinuation? {
        guard decision != .deny, refusal(card, decision: decision) == nil else { return nil }
        let allowed = card.actions.filter { $0.rule != nil }.map { $0.target ?? $0.displayName }
        let list = ActivitySupport.clean(allowed.joined(separator: ", "), maximumBytes: 2_048, singleLine: true)
        return AntigravityContinuation(grant: AntigravityAllowGrant(requestId: card.id, rules: card.rules, temporary: decision == .once),
                                       prompt: L("antigravity.permission.continue", ["actions": list]))
    }

    /// The short transcript line a card leaves (what an older phone shows).
    public static func transcriptLine(_ card: AntigravityPermissionRequest) -> String {
        L("run.notice.antigravityAsked", ["actions": ActivitySupport.clean(card.names.joined(separator: ", "), maximumBytes: 512, singleLine: true)])
    }

    /// What the card says an action's rule allows: commands starting with it,
    /// a file or everything in a folder, a domain.
    public static func ruleLine(_ action: AntigravityDeniedAction) -> String? {
        guard let rule = action.rule else { return nil }
        return L(action.action == "command" ? "antigravity.permission.ruleCommand" : "antigravity.permission.rule", ["rule": rule])
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
    /// "항상 허용" for a rule that may be allowed only once.
    case onceOnly

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
        case .onceOnly: return L("remote.error.antigravityOnceOnly")
        }
    }
}

/// Edits `permissions.allow` in the bytes of agy's settings, leaving every
/// other byte as it was: other keys, their order and values, unknown fields,
/// whitespace. Rules are added after the last one in the style of the list
/// (or of the file, for a list it has to create, with its line endings), and
/// removing one takes out exactly what adding it put in; `pruning` then takes
/// out the `permissions` or `allow` adding had to create, once empty.
public enum AntigravitySettingsText {
    /// What adding made that was not there: the file, `permissions` (with its `allow`) or `allow`.
    public enum Created: String, Codable, Sendable { case file, permissions, allow }

    /// The file's rules; throws when it is not an object, `permissions` is not
    /// an object or `allow` is not an array (non-string entries are skipped).
    /// A UTF-8 byte order mark is not JSON here (agy writes none).
    public static func rules(_ data: Data, path: String) throws -> [String] {
        guard !data.starts(with: [0xEF, 0xBB, 0xBF]) else { throw AntigravitySettingsError.notJSON(path) }
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

    /// `rules` not in the file yet, added. Returns the bytes, what was added
    /// and what had to be created for it.
    public static func adding(_ rules: [String], to data: Data, path: String) throws -> (data: Data, added: [String], created: [Created]) {
        let existing = Set(try Self.rules(data, path: path))
        var seen = Set<String>()
        let missing = rules.filter { !existing.contains($0) && seen.insert($0).inserted }
        guard !missing.isEmpty else { return (data, [], []) }
        var b = [UInt8](data)
        let newline = data.range(of: Data("\r\n".utf8)) == nil ? "\n" : "\r\n"
        func array(_ rules: [String], indent: String?, unit: String) -> String { Self.array(rules, indent: indent, unit: unit, newline: newline) }
        func allowObject(_ rules: [String], indent: String?, unit: String) -> String { Self.allowObject(rules, indent: indent, unit: unit, newline: newline) }
        let scan = Scanner(bytes: b, path: path)
        let top = scan.skipWhitespace(0)
        guard top < b.count, b[top] == UInt8(ascii: "{") else { throw AntigravitySettingsError.notObject(path) }
        let topObject = try scan.object(top)
        let unit = scan.lineIndent(topObject.members.first?.keyStart ?? -1).flatMap { $0.isEmpty ? nil : $0 } ?? "  "
        guard let permissions = topObject.members.last(where: { $0.key == "permissions" }) else {
            let text = "\"permissions\": "
            try insertMember(&b, scan: scan, object: topObject, indentOfObject: "", unit: unit, newline: newline) { indent in
                text + allowObject(missing, indent: indent, unit: unit)
            }
            return (Data(b), missing, [.permissions])
        }
        let permissionsIndent = scan.lineIndent(permissions.keyStart)
        let section = try scan.object(permissions.valueStart)
        guard let allow = section.members.last(where: { $0.key == "allow" }) else {
            try insertMember(&b, scan: scan, object: section, indentOfObject: permissionsIndent, unit: unit, newline: newline) { indent in
                "\"allow\": " + array(missing, indent: indent, unit: unit)
            }
            return (Data(b), missing, [.allow])
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
        return (Data(b), missing, [])
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

    /// Takes out what adding created once it is empty: `permissions` when it
    /// holds nothing but an empty `allow` (`.permissions`, `.file`), else an
    /// empty `allow` (`.allow`). `empty`: the file is now `{}`, which a
    /// created file is deleted for.
    public static func pruning(_ created: Set<Created>, from data: Data, path: String) throws -> (data: Data, empty: Bool) {
        _ = try Self.rules(data, path: path)
        var b = [UInt8](data)
        let scan = Scanner(bytes: b, path: path)
        let top = try scan.object(scan.skipWhitespace(0))
        let permissionMembers = top.members.indices.filter { top.members[$0].key == "permissions" }
        if permissionMembers.count == 1, let index = permissionMembers.first {
            let section = try scan.object(top.members[index].valueStart)
            let allows = section.members.indices.filter { section.members[$0].key == "allow" }
            let emptyAllow = try allows.count == 1 && scan.array(section.members[allows[0]].valueStart).elements.isEmpty
            if created.contains(.permissions) || created.contains(.file) {
                if section.members.isEmpty || (section.members.count == 1 && emptyAllow) { removeMember(&b, object: top, at: index) }
            } else if created.contains(.allow), emptyAllow {
                removeMember(&b, object: section, at: allows[0])
            }
        }
        let after = Scanner(bytes: b, path: path)
        let result = try after.object(after.skipWhitespace(0))
        _ = try Self.rules(Data(b), path: path)
        return (Data(b), result.members.isEmpty)
    }

    /// Takes a member out with the separator that adding put before it; the
    /// only member leaves `{}`.
    private static func removeMember(_ b: inout [UInt8], object: Scanner.Object, at index: Int) {
        let members = object.members
        if members.count == 1 { b.replaceSubrange(object.open...object.close, with: Array("{}".utf8)) }
        else if index == members.count - 1 { b.removeSubrange(members[index - 1].valueEnd..<members[index].valueEnd) }
        else { b.removeSubrange(members[index].keyStart..<members[index + 1].keyStart) }
    }

    // MARK: Rendering

    static func encoded(_ rule: String) -> [UInt8] {
        let data = (try? JSONSerialization.data(withJSONObject: rule, options: [.fragmentsAllowed, .withoutEscapingSlashes])) ?? Data("\"\"".utf8)
        return [UInt8](data)
    }
    /// A list on its own lines under `indent`, or on one line when the place it goes is one line.
    static func array(_ rules: [String], indent: String?, unit: String, newline: String = "\n") -> String {
        let items = rules.map { String(decoding: encoded($0), as: UTF8.self) }
        guard let indent else { return "[" + items.joined(separator: ", ") + "]" }
        return "[" + newline + items.map { indent + unit + $0 }.joined(separator: "," + newline) + newline + indent + "]"
    }
    static func allowObject(_ rules: [String], indent: String?, unit: String, newline: String = "\n") -> String {
        guard let indent else { return "{\"allow\": " + array(rules, indent: nil, unit: unit) + "}" }
        return "{" + newline + indent + unit + "\"allow\": " + array(rules, indent: indent + unit, unit: unit, newline: newline) + newline + indent + "}"
    }

    /// Adds a member to `object`: after its last member, in that member's
    /// style, or as the only member of an empty object. `render` gets the
    /// member's indent (nil when it goes on one line).
    private static func insertMember(_ b: inout [UInt8], scan: Scanner, object: Scanner.Object, indentOfObject: String?, unit: String, newline: String, render: (String?) -> String) throws {
        if let last = object.members.last {
            let previousEnd = object.members.count > 1 ? object.members[object.members.count - 2].valueEnd : object.open
            let separator = Array(b[scan.separatorEnd(before: last.keyStart, after: previousEnd)..<last.keyStart])
            let indent = scan.lineIndent(last.keyStart)
            b.insert(contentsOf: Array(",".utf8) + separator + Array(render(indent).utf8), at: last.valueEnd)
        } else if let outer = indentOfObject {
            let text = "{" + newline + outer + unit + render(outer + unit) + newline + outer + "}"
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
/// once the settings are seen without it a little after it was removed (a
/// running agy that rewrites its settings could bring it back); a removal
/// that fails is tried again with growing pauses, then again on the next
/// apply or release and at the next launch (`removeLeftovers`). A rule the
/// settings already had is never removed, and what adding had to create
/// (`permissions`, `allow`, the file) goes again once it is empty.
public actor AntigravityRuleLeases {
    public struct Lease: Sendable, Hashable {
        public let id = UUID()
        /// The rules this lease holds a count on (the ones the app added).
        public let rules: [String]
    }
    private struct LedgerEntry: Codable, Equatable { var settings: String; var rule: String }
    private struct CreatedEntry: Codable, Equatable { var settings: String; var part: AntigravitySettingsText.Created }
    private struct Ledger: Codable {
        var version = 1
        var entries: [LedgerEntry] = []
        var created: [CreatedEntry]?
        var isEmpty: Bool { entries.isEmpty && (created ?? []).isEmpty }
    }

    public let settingsURL: URL
    private let ledgerURL: URL?
    private let retryDelays: [Duration]
    private let recheckDelay: Duration
    private var counts: [String: Int] = [:]
    private var leases: Set<Lease> = []
    /// Rules removed and waiting to be looked for once more.
    private var awaitingRecheck: Set<String> = []
    private var rechecks: [UUID: Task<Void, Never>] = [:]
    /// Quit released everything: nothing is added any more.
    private var closed = false

    /// `~/.gemini/antigravity-cli/settings.json` (on Windows the user profile's same path).
    public static func defaultSettingsURL(home: URL = FileManager.default.homeDirectoryForCurrentUser) -> URL {
        home.appendingPathComponent(".gemini/antigravity-cli/settings.json")
    }

    /// `ledgerURL`: where the rules added for a while are recorded (the app's
    /// data folder); nil keeps no record (tests of the counting alone).
    /// `retryDelays`: the pauses before each new try of a failed removal;
    /// `recheckDelay`: how long after a removal the settings are looked at again.
    public init(settingsURL: URL = AntigravityRuleLeases.defaultSettingsURL(), ledgerURL: URL?,
                retryDelays: [Duration] = [.milliseconds(250), .seconds(1), .seconds(3)], recheckDelay: Duration = .seconds(5)) {
        self.settingsURL = settingsURL; self.ledgerURL = ledgerURL; self.retryDelays = retryDelays; self.recheckDelay = recheckDelay
    }

    /// The rules the app holds for a while now, with their counts.
    public func held() -> [String: Int] { counts }

    /// Reads the settings the way adding would, and changes nothing.
    public func check() throws {
        _ = try read()
    }

    /// Waits for the look-agains removals started (tests).
    public func settled() async {
        while let task = rechecks.values.first { await task.value }
    }

    /// Puts `grant`'s rules in place. A temporary grant returns the lease to
    /// `release` when its run ends; one for good returns nil. After quit's
    /// `releaseAll` it throws `CancellationError` and adds nothing.
    public func apply(_ grant: AntigravityAllowGrant) throws -> Lease? {
        guard !closed else { throw CancellationError() }
        guard grant.isValid else { throw AntigravitySettingsError.badRule }
        // Never a file rule into (or above) these settings' own folder or this ledger's, wherever they are.
        let guarded = AntigravityRuleSafety.protectedFolders() + ownFolders()
        guard grant.rules.allSatisfy({ AntigravityRuleSafety.verdict($0, protectedFolders: guarded, resolvingLinks: true) != .refused }) else {
            throw AntigravitySettingsError.badRule
        }
        guard grant.keepsOnlyAllowedRules else { throw AntigravitySettingsError.onceOnly }
        retryPending()
        let current = try read()
        let existing = Set(current.rules)
        var seen = Set<String>()
        let rules = grant.rules.filter { seen.insert($0).inserted }
        guard grant.temporary else {
            // Kept for good: a rule the app holds or held for a while is no longer its to remove.
            let missing = rules.filter { !existing.contains($0) }
            if !missing.isEmpty { try save(try changed(current, adding: missing).data) }
            for rule in rules { counts.removeValue(forKey: rule); awaitingRecheck.remove(rule) }
            try? forget(rules)
            return nil
        }
        // Still in the settings from an earlier hold whose removal is pending: held again.
        let pending = Set(ledgerRules())
        var incremented: [String] = []
        var reclaimed: [String] = []
        var added: [String] = []
        for rule in rules {
            if let count = counts[rule] { counts[rule] = count + 1; incremented.append(rule) }
            else if !existing.contains(rule) { added.append(rule) }
            else if pending.contains(rule) { counts[rule] = 1; awaitingRecheck.remove(rule); reclaimed.append(rule) }
        }
        if !added.isEmpty {
            do {
                let change = try changed(current, adding: added)
                try record(added, created: change.created)
                try save(change.data)
            } catch {
                try? forget(added)
                for rule in incremented { counts[rule] = (counts[rule] ?? 1) - 1 }
                for rule in reclaimed { counts.removeValue(forKey: rule) }
                throw error
            }
            for rule in added { counts[rule] = 1 }
        }
        let lease = Lease(rules: incremented + reclaimed + added)
        leases.insert(lease)
        return lease
    }

    /// The lease's run ended: a rule nobody holds any more leaves the settings.
    public func release(_ lease: Lease) async {
        guard leases.remove(lease) != nil else { return }
        var gone: [String] = []
        for rule in lease.rules {
            guard let count = counts[rule] else { continue }
            if count > 1 { counts[rule] = count - 1 } else { counts.removeValue(forKey: rule); gone.append(rule) }
        }
        retryPending(except: gone)
        await remove(gone)
    }

    /// Quit: every rule held for a while leaves now, and nothing is added after.
    public func releaseAll() async {
        closed = true
        let gone = Array(Set(Array(counts.keys) + ledgerRules().filter { !awaitingRecheck.contains($0) }))
        counts.removeAll(); leases.removeAll()
        await remove(gone)
    }

    /// Launch: what a previous run of the app added for a while and could not take out.
    public func removeLeftovers() async {
        let left = ledgerRules().filter { counts[$0] == nil && !awaitingRecheck.contains($0) }
        if left.isEmpty { pruneIfDone() } else { await remove(left) }
    }

    /// agy's folder these settings are in (`.gemini`, two up from
    /// `antigravity-cli/settings.json`) and the ledger's folder, as written and resolved.
    private func ownFolders() -> [String] {
        var urls = [settingsURL.deletingLastPathComponent().deletingLastPathComponent()]
        if let ledgerURL { urls.append(ledgerURL.deletingLastPathComponent()) }
        return urls.flatMap { [$0.path, $0.resolvingSymlinksInPath().path] }
    }

    // MARK: Removing

    /// Takes `rules` out of the settings, trying again after each of
    /// `retryDelays`; then looks again after `recheckDelay`. A rule held
    /// again meanwhile is left in.
    private func remove(_ rules: [String]) async {
        guard !rules.isEmpty else { return }
        if await removeWithRetries(rules) { scheduleRecheck(rules.filter { counts[$0] == nil }) }
    }

    private func removeWithRetries(_ rules: [String]) async -> Bool {
        var attempt = 0
        while true {
            let still = rules.filter { counts[$0] == nil }
            guard !still.isEmpty else { return true }
            do { try removeOnce(still); return true }
            catch {
                guard attempt < retryDelays.count else { return false }
                try? await Task.sleep(for: retryDelays[attempt])
                attempt += 1
            }
        }
    }

    private func removeOnce(_ rules: [String]) throws {
        let current = try read()
        guard let data = current.data else { return }
        let result = try AntigravitySettingsText.removing(rules, from: data, path: settingsURL.path)
        if !result.removed.isEmpty { try save(result.data) }
    }

    private func scheduleRecheck(_ rules: [String]) {
        guard !rules.isEmpty else { return }
        awaitingRecheck.formUnion(rules)
        let id = UUID()
        let delay = recheckDelay
        rechecks[id] = Task { [weak self] in
            try? await Task.sleep(for: delay)
            await self?.recheck(rules, id: id)
        }
    }

    /// A little after a removal: a rule agy wrote back is taken out again;
    /// then the ledger lets go of it.
    private func recheck(_ rules: [String], id: UUID) async {
        defer { rechecks.removeValue(forKey: id) }
        let ledger = Set(ledgerRules())
        let still = rules.filter { counts[$0] == nil && awaitingRecheck.contains($0) && ledger.contains($0) }
        awaitingRecheck.subtract(rules.filter { counts[$0] == nil })
        guard !still.isEmpty else { return }
        guard await removeWithRetries(still) else { return }
        let done = still.filter { counts[$0] == nil }
        do { try forget(done) } catch { return }
        pruneIfDone()
    }

    /// One try, without waiting, at what the ledger still holds that nobody
    /// holds now (an earlier removal that failed).
    private func retryPending(except: [String] = []) {
        let skip = Set(except)
        let pending = ledgerRules().filter { counts[$0] == nil && !awaitingRecheck.contains($0) && !skip.contains($0) }
        guard !pending.isEmpty, (try? removeOnce(pending)) != nil else { return }
        scheduleRecheck(pending)
    }

    /// Nothing held and nothing left in the ledger: what adding created goes once empty.
    private func pruneIfDone() {
        guard counts.isEmpty, ledgerRules().isEmpty, let ledger = readLedger() else { return }
        let path = settingsURL.path
        let created = Set((ledger.created ?? []).filter { $0.settings == path }.map(\.part))
        guard !created.isEmpty else { return }
        do {
            let current = try read()
            if let data = current.data {
                let result = try AntigravitySettingsText.pruning(created, from: data, path: path)
                let link = (try? FileManager.default.destinationOfSymbolicLink(atPath: path)) != nil
                if result.empty, created.contains(.file), !link { try FileManager.default.removeItem(at: settingsURL) }
                else if result.data != data { try save(result.data) }
            }
            var next = ledger
            next.created = (next.created ?? []).filter { $0.settings != path }
            try writeLedger(next)
        } catch {}
    }

    // MARK: Files

    private struct Current { var data: Data?; var rules: [String] }

    private func read() throws -> Current {
        let path = settingsURL.path
        let manager = FileManager.default
        // A link to nothing is not replaced by a new file.
        if (try? manager.destinationOfSymbolicLink(atPath: path)) != nil, !manager.fileExists(atPath: path) { throw AntigravitySettingsError.unreadable(path) }
        let target = settingsURL.resolvingSymlinksInPath()
        var isDirectory: ObjCBool = false
        guard manager.fileExists(atPath: target.deletingLastPathComponent().path, isDirectory: &isDirectory), isDirectory.boolValue else { throw AntigravitySettingsError.missingFolder(target.deletingLastPathComponent().path) }
        guard manager.fileExists(atPath: target.path) else { return Current(data: nil, rules: []) }
        guard let data = try? Data(contentsOf: target) else { throw AntigravitySettingsError.unreadable(path) }
        return Current(data: data, rules: try AntigravitySettingsText.rules(data, path: path))
    }

    /// The settings with `rules` added, and what that created.
    private func changed(_ current: Current, adding rules: [String]) throws -> (data: Data, created: [AntigravitySettingsText.Created]) {
        guard let data = current.data else { return (AntigravitySettingsText.newFile(rules), [.file]) }
        let result = try AntigravitySettingsText.adding(rules, to: data, path: settingsURL.path)
        return (result.data, result.created)
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
    /// The rules the ledger holds for these settings.
    private func ledgerRules() -> [String] {
        let path = settingsURL.path
        var seen = Set<String>()
        return (readLedger()?.entries ?? []).filter { $0.settings == path && seen.insert($0.rule).inserted }.map(\.rule)
    }
    private func writeLedger(_ ledger: Ledger) throws {
        guard let ledgerURL else { return }
        if ledger.isEmpty { try? FileManager.default.removeItem(at: ledgerURL); return }
        try FileManager.default.createDirectory(at: ledgerURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        let encoder = JSONEncoder(); encoder.outputFormatting = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
        try encoder.encode(ledger).write(to: ledgerURL, options: .atomic)
    }
    private func record(_ rules: [String], created: [AntigravitySettingsText.Created]) throws {
        guard let ledgerURL else { return }
        var ledger = readLedger() ?? Ledger()
        for rule in rules where !ledger.entries.contains(LedgerEntry(settings: settingsURL.path, rule: rule)) {
            ledger.entries.append(LedgerEntry(settings: settingsURL.path, rule: rule))
        }
        for part in created where !(ledger.created ?? []).contains(CreatedEntry(settings: settingsURL.path, part: part)) {
            ledger.created = (ledger.created ?? []) + [CreatedEntry(settings: settingsURL.path, part: part)]
        }
        do { try writeLedger(ledger) } catch { throw AntigravitySettingsError.writeFailed(ledgerURL.path, error.localizedDescription) }
    }
    private func forget(_ rules: [String]) throws {
        guard ledgerURL != nil, !rules.isEmpty, var ledger = readLedger() else { return }
        let gone = Set(rules)
        ledger.entries.removeAll { $0.settings == settingsURL.path && gone.contains($0.rule) }
        try writeLedger(ledger)
    }
}
