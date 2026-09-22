import Foundation

/// The numbers of docs/mighty-styles.md §1.11 live here and nowhere else.
public enum StyleLimits {
    public static let maximumBytes = 262_144
    public static let maximumActions = 100
    public static let maximumGroups = 16
    public static let maximumPhases = 16
    public static let maximumAliases = 64
    public static let maximumAutoAllow = 32
    public static let maximumCapabilities = 4
    public static let maximumProbes = 16
    public static let maximumPrefixes = 4
    public static let maximumDepth = 8
    public static let maximumFilesPerSource = 32
    public static let maximumDirectoryEntries = 1024
    public static let maximumApprovalRecords = 256
    public static let maximumCasebookChips = 6
    public static let maximumCasebookFolders = 24
    public static let maximumCasebookFiles = 24
    /// No manifest string is longer than this, whatever the field.
    public static let maximumString = 400
    /// A JSON key name. Schema 1's own keys are short words and the only
    /// author-chosen keys are ids, which stop at 40 (§1.11).
    public static let maximumKey = 64
    /// A value quoted back inside an error message (§2).
    public static let maximumMessageValue = 64
    /// A capability string projected to a screen (§1.8).
    public static let maximumCapabilityString = 80
}

/// One refusal: the frozen code and the Korean line both surfaces show.
public struct StyleManifestError: Error, Sendable, Equatable {
    public var code: String
    public var message: String
    public init(code: String, message: String) { self.code = code; self.message = message }
}

/// The characters §1.11 bans from every manifest string and key, and the
/// substitutions §2 and §1.8 make before such a value reaches a screen.
public enum StyleText {
    static let bannedRanges: [ClosedRange<UInt32>] = [
        0x0000...0x001F, 0x007F...0x009F, 0x00AD...0x00AD, 0x061C...0x061C,
        0x200B...0x200F, 0x202A...0x202E, 0x2028...0x2029, 0x2060...0x2060,
        0x2066...0x2069, 0xFEFF...0xFEFF,
    ]
    public static func isBanned(_ scalar: Unicode.Scalar) -> Bool {
        bannedRanges.contains { $0.contains(scalar.value) }
    }
    public static func containsBanned(_ value: String) -> Bool { value.unicodeScalars.contains(where: isBanned) }

    /// Banned scalars become U+FFFD; nothing else changes.
    public static func replacingBanned(_ value: String) -> String {
        guard containsBanned(value) else { return value }
        var result = String.UnicodeScalarView()
        for scalar in value.unicodeScalars { result.append(isBanned(scalar) ? "\u{FFFD}" : scalar) }
        return String(result)
    }

    /// A value quoted inside an error message: the attacker wrote it, so it is
    /// cleaned and cut before the app repeats it (§2).
    public static func safe(_ value: String) -> String { truncated(replacingBanned(value), to: StyleLimits.maximumMessageValue) }

    /// A string that came off the file system rather than through the approval
    /// card, normalised once just before projection (§1.8).
    public static func normalised(_ value: String, limit: Int? = nil) -> String {
        let cleaned = replacingBanned(value)
        return limit.map { truncated(cleaned, to: $0) } ?? cleaned
    }

    /// The ellipsis is part of the budget: a value "cut to 64" is 64 characters
    /// on screen, not 65 (§2, §1.8).
    static func truncated(_ value: String, to limit: Int) -> String {
        guard value.count > limit, limit > 0 else { return value }
        return String(value.prefix(limit - 1)) + "\u{2026}"
    }

    /// §1.2's comparison: NFKC, case folding, then every space removed.
    public static func folded(_ value: String) -> String {
        (value.precomposedStringWithCompatibilityMapping.lowercased()).filter { !$0.isWhitespace }
    }

    /// A glyph is exactly one grapheme cluster with an emoji presentation (§1.10).
    public static func isEmojiGlyph(_ value: String) -> Bool {
        guard value.count == 1, let character = value.first, let first = character.unicodeScalars.first, first.properties.isEmoji else { return false }
        return first.properties.isEmojiPresentation || character.unicodeScalars.contains("\u{FE0F}")
    }
}

/// Every refusal of §2, with the interpolated values already bounded.
public enum StyleErrors {
    static func code(_ code: String, _ message: String) -> StyleManifestError { StyleManifestError(code: code, message: message) }

    public static let tooLarge = code("E_TOO_LARGE", "매니페스트 파일이 256 KB를 넘습니다.")
    public static let tooDeep = code("E_TOO_DEEP", "JSON 중첩이 너무 깊습니다 (최대 8단계).")
    public static func duplicateKey(_ path: String) -> StyleManifestError { code("E_DUPLICATE_KEY", "같은 항목이 두 번 적혀 있습니다: \(StyleText.safe(path)).") }
    public static let schemaNotFirst = code("E_SCHEMA_NOT_FIRST", "schema는 파일의 첫 항목이어야 합니다.")
    public static func keyEscape(_ path: String) -> StyleManifestError {
        code("E_KEY_ESCAPE", "항목 이름에는 이스케이프를 쓸 수 없습니다: \(StyleText.safe(path)).")
    }
    public static let notJSON = code("E_NOT_JSON", "JSON 형식이 아닙니다.")
    public static let schemaMissing = code("E_SCHEMA_MISSING", "schema 필드가 없습니다.")
    public static let schemaVersion = code("E_SCHEMA_VERSION", "이 앱은 schema 1만 읽습니다.")
    public static func unknownField(_ path: String) -> StyleManifestError { code("E_UNKNOWN_FIELD", "알 수 없는 항목이 있습니다: \(StyleText.safe(path)).") }
    public static func missingField(_ path: String) -> StyleManifestError { code("E_MISSING_FIELD", "필수 항목이 없습니다: \(StyleText.safe(path)).") }
    public static func type(_ path: String) -> StyleManifestError { code("E_TYPE", "항목의 형식이 올바르지 않습니다: \(StyleText.safe(path)).") }
    public static func stringLength(_ path: String) -> StyleManifestError { code("E_STRING_LENGTH", "글자 수 한도를 넘었습니다: \(StyleText.safe(path)).") }
    public static func controlChar(_ path: String) -> StyleManifestError { code("E_CONTROL_CHAR", "표시할 수 없는 제어 문자가 들어 있습니다: \(StyleText.safe(path)).") }
    public static func reservedSeparator(_ path: String) -> StyleManifestError { code("E_RESERVED_SEPARATOR", "이 항목에는 `\u{00B7}`를 쓸 수 없습니다: \(StyleText.safe(path)).") }
    public static func idShape(_ value: String) -> StyleManifestError { code("E_ID_SHAPE", "id 형식이 올바르지 않습니다: \(StyleText.safe(value)).") }
    public static func reservedId(_ value: String) -> StyleManifestError { code("E_RESERVED_ID", "\(StyleText.safe(value))은 앱이 예약한 스타일 id입니다.") }
    public static func reservedName(_ value: String) -> StyleManifestError { code("E_RESERVED_NAME", "\(StyleText.safe(value))은 앱이 예약한 스타일 이름입니다.") }
    public static func duplicateId(_ path: String, _ value: String) -> StyleManifestError {
        code("E_DUPLICATE_ID", "같은 id가 두 번 있습니다: \(StyleText.safe(path)) \u{2192} \(StyleText.safe(value)).")
    }
    public static func limit(_ path: String, _ maximum: Int) -> StyleManifestError { code("E_LIMIT", "개수 한도를 넘었습니다: \(StyleText.safe(path)) (최대 \(maximum)개).") }
    public static func promptPlaceholder(_ id: String) -> StyleManifestError { code("E_PROMPT_PLACEHOLDER", "프롬프트에는 `{text}` 하나만 쓸 수 있습니다: \(StyleText.safe(id)).") }
    public static func takesTextMismatch(_ id: String) -> StyleManifestError { code("E_TAKES_TEXT_MISMATCH", "takesText와 프롬프트의 `{text}` 유무가 다릅니다: \(StyleText.safe(id)).") }
    public static func foldText(_ id: String) -> StyleManifestError { code("E_FOLD_TEXT", "입력 글을 받지 않는 행동에는 foldText를 쓸 수 없습니다: \(StyleText.safe(id)).") }
    public static func promptRecognition(_ id: String) -> StyleManifestError {
        code("E_PROMPT_RECOGNITION", "이 행동의 프롬프트는 인식 규칙으로 자기 이름이 되지 않습니다: \(StyleText.safe(id)).")
    }
    public static func unknownFlag(_ value: String) -> StyleManifestError { code("E_UNKNOWN_FLAG", "알 수 없는 flag입니다: \(StyleText.safe(value)).") }
    public static func unknownReference(_ path: String, _ value: String) -> StyleManifestError {
        code("E_UNKNOWN_REFERENCE", "없는 항목을 가리킵니다: \(StyleText.safe(path)) \u{2192} \(StyleText.safe(value)).")
    }
    public static func aliasCollision(_ value: String) -> StyleManifestError { code("E_ALIAS_COLLISION", "별칭 이름이 행동 id와 겹칩니다: \(StyleText.safe(value)).") }
    public static func unknownRule(_ path: String, _ value: String) -> StyleManifestError {
        code("E_UNKNOWN_RULE", "알 수 없는 규칙 종류입니다: \(StyleText.safe(path)) \u{2192} \(StyleText.safe(value)).")
    }
    public static func ruleIncomplete(_ value: String) -> StyleManifestError { code("E_RULE_INCOMPLETE", "규칙이 모든 단계를 다루지 않습니다: \(StyleText.safe(value)).") }
    public static func startPhase(_ value: String) -> StyleManifestError { code("E_START_PHASE", "시작 규칙이 없는 단계를 가리킵니다: \(StyleText.safe(value)).") }
    public static let phaseRuleNone = code("E_PHASE_RULE_NONE", "단계가 있는데 단계 규칙이 none입니다.")
    public static func enterActionText(_ id: String) -> StyleManifestError {
        code("E_ENTER_ACTION_TEXT", "Enter 규칙이 가리키는 행동은 입력 글을 받아야 합니다: \(StyleText.safe(id)).")
    }
    public static func unknownCapability(_ value: String) -> StyleManifestError { code("E_UNKNOWN_CAPABILITY", "이 앱이 모르는 내장 기능입니다: \(StyleText.safe(value)).") }
    public static func capabilityUndeclared(_ value: String) -> StyleManifestError {
        code("E_CAPABILITY_UNDECLARED", "capabilities에 선언하지 않은 내장 기능을 씁니다: \(StyleText.safe(value)).")
    }
    public static func capabilityMap(_ value: String) -> StyleManifestError { code("E_CAPABILITY_MAP", "내장 기능의 상태 값이 모두 매핑되지 않았습니다: \(StyleText.safe(value)).") }
    public static func unknownTint(_ value: String) -> StyleManifestError { code("E_UNKNOWN_TINT", "앱 팔레트에 없는 색 이름입니다: \(StyleText.safe(value)).") }
    public static func unknownIcon(_ value: String) -> StyleManifestError { code("E_UNKNOWN_ICON", "앱이 제공하지 않는 아이콘 이름입니다: \(StyleText.safe(value)).") }
    public static func unknownProbe(_ value: String) -> StyleManifestError { code("E_UNKNOWN_PROBE", "알 수 없는 준비물 검사 종류입니다: \(StyleText.safe(value)).") }
    public static func probeNameShape(_ value: String) -> StyleManifestError { code("E_PROBE_NAME_SHAPE", "준비물 이름 형식이 올바르지 않습니다: \(StyleText.safe(value)).") }
    public static func scopes(_ value: String) -> StyleManifestError { code("E_SCOPES", "scopes는 user\u{00B7}workspace 중 1–2개여야 합니다: \(StyleText.safe(value)).") }
    public static func autoAllowServer(_ tool: String) -> StyleManifestError {
        code("E_AUTOALLOW_SERVER", "자동 허용에는 서버 이름이 필요합니다 (ToolSearch만 예외): \(StyleText.safe(tool)).")
    }
    public static func autoAllowShape(_ value: String) -> StyleManifestError { code("E_AUTOALLOW_SHAPE", "자동 허용 이름에는 패턴을 쓸 수 없습니다: \(StyleText.safe(value)).") }
    public static func autoAllowForeignServer(_ wire: String) -> StyleManifestError {
        code("E_AUTOALLOW_FOREIGN_SERVER", "자동 허용은 이 스타일이 요구하는 플러그인의 도구만 쓸 수 있습니다: \(StyleText.safe(wire)).")
    }
    public static let autoAllowToolSearchBundled = code("E_AUTOALLOW_TOOLSEARCH_BUNDLED", "ToolSearch는 내장 스타일만 자동 허용할 수 있습니다.")
    public static let autoAllowQuestion = code("E_AUTOALLOW_QUESTION", "AskUserQuestion은 자동 허용할 수 없습니다.")
    public static func autoAllowDuplicate(_ wire: String) -> StyleManifestError { code("E_AUTOALLOW_DUPLICATE", "같은 도구가 두 번 있습니다: \(StyleText.safe(wire)).") }
    public static let placeholderInitial = code("E_PLACEHOLDER_INITIAL", "initial placeholder는 Enter 규칙이 rewriteBareDraftTo일 때만 쓸 수 있습니다.")
    public static func jobMatcherLiteral(_ path: String) -> StyleManifestError {
        code("E_JOB_MATCHER_LITERAL", "매처에는 contains 또는 notContains 중 하나만 쓸 수 있습니다: \(StyleText.safe(path)).")
    }
    public static func idCollision(_ id: String, _ winner: StyleSource) -> StyleManifestError {
        code("E_ID_COLLISION", "이미 같은 id의 스타일이 있습니다: \(StyleText.safe(id)) (\(winner.rawValue)).")
    }
}

/// The frozen code list of §2, read off the values `StyleErrors` actually
/// produces rather than written out again: adding a refusal without adding it
/// here does not compile, and a code that exists only in a test is not a code.
public enum StyleErrorCodes {
    static let produced: [StyleManifestError] = [
        StyleErrors.tooLarge, StyleErrors.tooDeep, StyleErrors.duplicateKey(""), StyleErrors.schemaNotFirst,
        StyleErrors.keyEscape(""), StyleErrors.notJSON, StyleErrors.schemaMissing, StyleErrors.schemaVersion,
        StyleErrors.unknownField(""), StyleErrors.missingField(""), StyleErrors.type(""), StyleErrors.stringLength(""),
        StyleErrors.controlChar(""), StyleErrors.reservedSeparator(""), StyleErrors.idShape(""), StyleErrors.reservedId(""),
        StyleErrors.reservedName(""), StyleErrors.duplicateId("", ""), StyleErrors.limit("", 0),
        StyleErrors.promptPlaceholder(""), StyleErrors.takesTextMismatch(""), StyleErrors.foldText(""),
        StyleErrors.promptRecognition(""), StyleErrors.unknownFlag(""), StyleErrors.unknownReference("", ""),
        StyleErrors.aliasCollision(""), StyleErrors.unknownRule("", ""), StyleErrors.ruleIncomplete(""),
        StyleErrors.startPhase(""), StyleErrors.phaseRuleNone, StyleErrors.enterActionText(""),
        StyleErrors.unknownCapability(""), StyleErrors.capabilityUndeclared(""), StyleErrors.capabilityMap(""),
        StyleErrors.unknownTint(""), StyleErrors.unknownIcon(""), StyleErrors.unknownProbe(""),
        StyleErrors.probeNameShape(""), StyleErrors.scopes(""), StyleErrors.autoAllowServer(""),
        StyleErrors.autoAllowShape(""), StyleErrors.autoAllowForeignServer(""), StyleErrors.autoAllowToolSearchBundled,
        StyleErrors.autoAllowQuestion, StyleErrors.autoAllowDuplicate(""), StyleErrors.placeholderInitial,
        StyleErrors.jobMatcherLiteral(""),
        StyleErrors.idCollision("", .user),
    ]
    public static let all: Set<String> = Set(produced.map(\.code))
}

public enum StyleFold: String, Sendable, Equatable, CaseIterable { case trimOnly, oneLine }
public enum StyleActionFlag: String, Sendable, Equatable, CaseIterable { case userInvoked, readOnly }
public enum StyleScope: String, Sendable, Equatable, CaseIterable { case user, workspace }

public enum StyleTint: String, Sendable, Codable, Equatable, CaseIterable {
    case accent, purple, teal, indigo, mint, orange, green, red, secondary
}

/// A closed list, frozen with the tag: MightyCore cannot check an SF Symbol
/// exists, and an open list would let a manifest borrow the app's own
/// permission and error vocabulary (§1.10).
public struct StyleIcon: Sendable, Equatable, Hashable, Codable, RawRepresentable {
    public let rawValue: String
    public init?(rawValue: String) {
        guard StyleIcon.all.contains(rawValue) else { return nil }
        self.rawValue = rawValue
    }
    public static let all: [String] = [
        "point.3.connected.trianglepath.dotted", "questionmark.bubble", "wand.and.stars", "leaf", "play.fill",
        "checkmark.seal", "arrow.triangle.2.circlepath", "infinity", "gauge.with.dots.needle.33percent", "lightbulb",
        "square.grid.2x2",
        "arrow.up.message", "questionmark.square.dashed",
        "arrow.right", "arrow.clockwise", "arrow.triangle.branch", "bolt", "book", "bookmark", "calendar",
        "chart.bar", "cube", "doc.text", "flag", "folder", "hammer", "list.bullet", "magnifyingglass", "map",
        "paintbrush", "puzzlepiece", "sparkles", "tray",
    ]
    public static let requestDefault = StyleIcon(rawValue: "arrow.up.message")!
}

public struct StylePhase: Sendable, Equatable, Identifiable {
    public var id, title: String
    public var order: Int
    public init(id: String, title: String, order: Int) { self.id = id; self.title = title; self.order = order }
}

public struct StyleGroup: Sendable, Equatable, Identifiable {
    public var id, title: String
    public var axis, question: String?
    public var actions: [String]
    public init(id: String, title: String, axis: String? = nil, question: String? = nil, actions: [String]) {
        self.id = id; self.title = title; self.axis = axis; self.question = question; self.actions = actions
    }
}

public struct StyleAlias: Sendable, Equatable {
    public var name, phase: String
    public init(name: String, phase: String) { self.name = name; self.phase = phase }
}

public struct StyleAction: Sendable, Equatable, Identifiable {
    public var id, title, help: String
    public var scope: String?
    public var prompt: String
    public var takesText, requiresText: Bool
    public var foldText: StyleFold?
    public var match: String?
    public var phase: String?
    public var flags: Set<StyleActionFlag>
    public var icon: StyleIcon?
    public var glyph: String?
    public var tint: StyleTint?
    public var requestTitle: String?
    public init(id: String, title: String, help: String, scope: String? = nil, prompt: String, takesText: Bool, requiresText: Bool = false,
                foldText: StyleFold? = nil, match: String? = nil, phase: String? = nil, flags: Set<StyleActionFlag> = [],
                icon: StyleIcon? = nil, glyph: String? = nil, tint: StyleTint? = nil, requestTitle: String? = nil) {
        self.id = id; self.title = title; self.help = help; self.scope = scope; self.prompt = prompt
        self.takesText = takesText; self.requiresText = requiresText; self.foldText = foldText; self.match = match
        self.phase = phase; self.flags = flags; self.icon = icon; self.glyph = glyph; self.tint = tint; self.requestTitle = requestTitle
    }

    /// The prompt with `{text}` filled in, or removed with the spaces that ran
    /// up to it when the folded text is empty (§1.3.1).
    public func prompt(text: String) -> String {
        let folded = foldText.map { $0.fold(text) } ?? ""
        let template = self.prompt
        guard let range = template.range(of: "{text}") else { return template }
        guard folded.isEmpty else { return template.replacingCharacters(in: range, with: folded) }
        var start = range.lowerBound
        while start > template.startIndex, template[template.index(before: start)] == " " { start = template.index(before: start) }
        return template.replacingCharacters(in: start..<range.upperBound, with: "")
    }
}

extension StyleFold {
    public func fold(_ text: String) -> String {
        switch self {
        case .trimOnly: return text.trimmingCharacters(in: .whitespacesAndNewlines)
        case .oneLine: return text.split(whereSeparator: \.isNewline).map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }.joined(separator: " ")
        }
    }
}

public struct StyleRecognition: Sendable, Equatable {
    public var prefixes: [String]
    public var lowercase: Bool
    public init(prefixes: [String], lowercase: Bool) { self.prefixes = prefixes; self.lowercase = lowercase }

    /// The bare name a past request carries: the first matching prefix is cut
    /// and the word up to the first space is the name (§1.4).
    public func name(inPrompt prompt: String) -> String? {
        let trimmed = prompt.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let prefix = prefixes.first(where: trimmed.hasPrefix) else { return nil }
        let rest = trimmed.dropFirst(prefix.count)
        let name = String(rest.prefix { !$0.isWhitespace })
        guard !name.isEmpty else { return nil }
        return lowercase ? name.lowercased() : name
    }
}

public struct StylePlaceholders: Sendable, Equatable {
    public var idle, answering: String
    public var initial, running: String?
    public init(idle: String, answering: String, initial: String? = nil, running: String? = nil) {
        self.idle = idle; self.answering = answering; self.initial = initial; self.running = running
    }
}

public struct StyleGuidance: Sendable, Equatable {
    public var start, next, running: String?
    public init(start: String? = nil, next: String? = nil, running: String? = nil) { self.start = start; self.next = next; self.running = running }
}

public struct StyleInstall: Sendable, Equatable {
    public var command, paneTitle: String
    public init(command: String, paneTitle: String) { self.command = command; self.paneTitle = paneTitle }
}

public struct StylePresentation: Sendable, Equatable {
    public var icon: StyleIcon?
    public var tint: StyleTint?
    public init(icon: StyleIcon? = nil, tint: StyleTint? = nil) { self.icon = icon; self.tint = tint }
}

public enum StyleProbe: Sendable, Equatable {
    case plugin(prefix: String, missing: String, hint: String?, install: Bool)
    case executable(name: String, missing: String, hint: String?, install: Bool)
    case skill(name: String, scopes: [StyleScope], missing: String, hint: String?, install: Bool)

    public var missing: String {
        switch self { case .plugin(_, let m, _, _), .executable(_, let m, _, _), .skill(_, _, let m, _, _): return m }
    }
    public var hint: String? {
        switch self { case .plugin(_, _, let h, _), .executable(_, _, let h, _), .skill(_, _, _, let h, _): return h }
    }
    public var install: Bool {
        switch self { case .plugin(_, _, _, let i), .executable(_, _, _, let i), .skill(_, _, _, _, let i): return i }
    }
    /// The plugin name a `plugin` probe requires, `ouroboros@` → `ouroboros`
    /// (§1.9). Only a prefix that ends with `@` names a whole plugin: a bare
    /// prefix matches installed keys by `hasPrefix`, so `a` would also claim
    /// the plugin `a_b` and with it every `plugin_a_b_*` server.
    public var pluginName: String? {
        guard case .plugin(let prefix, _, _, _) = self, prefix.hasSuffix("@") else { return nil }
        return String(prefix.dropLast())
    }
}

public struct StylePrerequisites: Sendable, Equatable {
    public enum Mode: String, Sendable, Equatable { case all, any }
    public enum Report: String, Sendable, Equatable { case first, all }
    public var mode: Mode
    public var report: Report
    public var probes: [StyleProbe]
    public init(mode: Mode, report: Report = .first, probes: [StyleProbe]) { self.mode = mode; self.report = report; self.probes = probes }
}

public struct StylePrerequisiteResult: Sendable, Equatable {
    public var ready: Bool
    public var missing: [String]
    public var hint: String?
    public var canInstall: Bool
    public init(ready: Bool, missing: [String] = [], hint: String? = nil, canInstall: Bool = false) {
        self.ready = ready; self.missing = missing; self.hint = hint; self.canInstall = canInstall
    }
}

public struct StyleAutoAllowEntry: Sendable, Equatable {
    public var server: String?
    public var tool: String
    public var wireName: String
    public init(server: String?, tool: String) {
        self.server = server; self.tool = tool
        self.wireName = server.map { "mcp__" + $0 + "__" + tool } ?? tool
    }
}

public enum StylePhaseRule: Sendable, Equatable { case none, lastRecognisedAction(default: String) }
public enum StyleStartRule: Sendable, Equatable { case none, actions(phase: String, actions: [String], resetTitle: String?) }
public enum StyleNextRule: Sendable, Equatable { case byPhase([String: [String]]), byGroup }
public enum StyleEnterRule: Sendable, Equatable { case verbatim, rewriteBareDraftTo(action: String, phase: String) }
public enum StyleRecommendRule: Sendable, Equatable { case none, capability(name: String, map: [String: String], group: String?) }
public enum StyleInitialGroupRule: Sendable, Equatable { case fixed(group: String), capabilityState(name: String, map: [String: String]) }

public struct StyleRules: Sendable, Equatable {
    public var start: StyleStartRule
    public var phase: StylePhaseRule
    public var next: StyleNextRule
    public var enter: StyleEnterRule
    public var recommend: StyleRecommendRule
    public var initialGroup: StyleInitialGroupRule
    public init(start: StyleStartRule, phase: StylePhaseRule, next: StyleNextRule, enter: StyleEnterRule,
                recommend: StyleRecommendRule, initialGroup: StyleInitialGroupRule) {
        self.start = start; self.phase = phase; self.next = next; self.enter = enter
        self.recommend = recommend; self.initialGroup = initialGroup
    }
}

/// One entry in a job declaration's open or close list: the tool name whose
/// result triggers the state change, with an optional literal that must (or
/// must not) appear in the result text (§1.13).
public struct StyleJobMatcher: Sendable, Equatable {
    public var tool: String
    public var contains: String?
    public var notContains: String?
    public init(tool: String, contains: String? = nil, notContains: String? = nil) {
        self.tool = tool; self.contains = contains; self.notContains = notContains
    }
}

/// Optional manifest field that makes the panel aware of background jobs
/// (§1.13). Manifests without this field behave exactly as before.
public struct StyleJobDeclaration: Sendable, Equatable {
    public var open: [StyleJobMatcher]
    public var close: [StyleJobMatcher]
    public var whileOpen: [String]
    public var guidance: String?
    public init(open: [StyleJobMatcher], close: [StyleJobMatcher], whileOpen: [String], guidance: String? = nil) {
        self.open = open; self.close = close; self.whileOpen = whileOpen; self.guidance = guidance
    }
}

public struct StyleManifest: Sendable, Equatable {
    public var schema: Int
    public var id, name, summary, subtitle: String
    public var placeholders: StylePlaceholders
    public var guidance: StyleGuidance
    public var prerequisites: StylePrerequisites
    public var install: StyleInstall?
    public var phases: [StylePhase]
    public var groups: [StyleGroup]
    public var actions: [StyleAction]
    public var aliases: [StyleAlias]
    public var recognition: StyleRecognition
    public var rules: StyleRules
    public var capabilities: [String]
    public var autoAllow: [StyleAutoAllowEntry]
    public var presentation: StylePresentation
    public var job: StyleJobDeclaration?
    public init(schema: Int, id: String, name: String, summary: String, subtitle: String, placeholders: StylePlaceholders,
                guidance: StyleGuidance, prerequisites: StylePrerequisites, install: StyleInstall?, phases: [StylePhase],
                groups: [StyleGroup], actions: [StyleAction], aliases: [StyleAlias], recognition: StyleRecognition,
                rules: StyleRules, capabilities: [String], autoAllow: [StyleAutoAllowEntry], presentation: StylePresentation,
                job: StyleJobDeclaration? = nil) {
        self.schema = schema; self.id = id; self.name = name; self.summary = summary; self.subtitle = subtitle
        self.placeholders = placeholders; self.guidance = guidance; self.prerequisites = prerequisites; self.install = install
        self.phases = phases; self.groups = groups; self.actions = actions; self.aliases = aliases
        self.recognition = recognition; self.rules = rules; self.capabilities = capabilities
        self.autoAllow = autoAllow; self.presentation = presentation; self.job = job
    }

    public func action(_ id: String) -> StyleAction? { actions.first { $0.id == id } }
    public func phase(_ id: String) -> StylePhase? { phases.first { $0.id == id } }
    public func group(_ id: String) -> StyleGroup? { groups.first { $0.id == id } }
    /// The phase bar's order, which is `order` ascending rather than file order.
    public var orderedPhases: [StylePhase] { phases.sorted { $0.order < $1.order } }
}
